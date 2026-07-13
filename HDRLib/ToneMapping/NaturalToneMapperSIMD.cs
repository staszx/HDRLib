// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Settings;

internal sealed class NaturalToneMapperSIMD : ToneMapperSIMD
{
    private const float ToneBoostSensitivity = 1f;
    private readonly NaturalToneMapperSettings settings;
    private readonly WhiteBalancerSIMD whiteBalancer = new();

    public NaturalToneMapperSIMD(NaturalToneMapperSettings settings) : base(settings)
    {
        this.settings = settings;
    }

    protected override bool AppliesToneBoostInternally => true;

    protected override bool NormalizesInputRange => false;

    protected override void ApplyCoreInPlace(Vector256<float>[][] pixels, int width, int height)
    {
        var count = width * height;
        if (count == 0)
        {
            return;
        }

        var lum = ToneMapperSIMDHelper.BuildLuminance(pixels[0], pixels[1], pixels[2], count);

        var logSum = 0.0;
        var maxLum = 0f;
        for (var i = 0; i < lum.Length; i++)
        {
            var l = MathF.Max(lum[i], 1e-6f);
            logSum += MathF.Log(l);
            if (l > maxLum)
            {
                maxLum = l;
            }
        }

        var logAverage = MathF.Exp((float)(logSum / lum.Length));
        Array.Sort(lum);
        var whiteLum = ToneMapperSIMDHelper.Percentile(lum, this.settings.WhitePointPercentile);
        var exposureCompensation = MathF.Pow(2f, this.settings.ExposureEV);
        if (!this.ForceToneMappingCore &&
            this.settings.BypassToneCompressionForLdr &&
            whiteLum <= this.settings.LdrBypassWhitePointThreshold &&
            maxLum <= this.settings.LdrBypassWhitePointThreshold)
        {
            var ldrBrightnessCompensation = this.settings.AutoBrightnessCompensation
                ? ComputeBrightnessCompensation(this.settings.OutputMidGray, logAverage * exposureCompensation)
                : 1f;
            ApplyLdrBypassAdjustments(pixels, exposureCompensation * ldrBrightnessCompensation);
            ApplyWhiteBalanceIfEnabled(pixels, width, height);
            ApplyGamma(pixels);
            return;
        }

        var compensationExposure = MathF.Max(this.settings.TargetGray, 0.01f) / MathF.Max(logAverage, 1e-6f);
        var exposure = compensationExposure * exposureCompensation;

        var whitePoint = MathF.Max(whiteLum * compensationExposure, 1e-3f);
        var whitePointSquared = whitePoint * whitePoint;
        var tonalRangeCompression = MathF.Max(this.settings.TonalRangeCompression, 1e-3f);
        var adjustedWhitePointSquared = whitePointSquared * tonalRangeCompression;
        var dynamicRangeFactor = maxLum / (maxLum + logAverage + 1e-6f);
        var contrastAdaptation = 1f;
        var adaptiveContrastFactor = (1f - contrastAdaptation) + (contrastAdaptation * (0.35f + (0.65f * dynamicRangeFactor)));
        var adaptiveContrast = 1f + ((this.settings.Contrast - 1f) * adaptiveContrastFactor);
        var baseSaturation = MathF.Max(0f, SaturationToMultiplier(this.settings.Saturation));
        var adaptiveSaturation = baseSaturation <= 1f
            ? baseSaturation
            : 1f + ((baseSaturation - 1f) * (0.4f + (0.6f * dynamicRangeFactor)));
        var compensationWhitePoint = MathF.Max(whiteLum * compensationExposure, 1e-3f);
        var compensationWhitePointSquared = (compensationWhitePoint * compensationWhitePoint) * tonalRangeCompression;
        var mappedAverage = CompressScalar(MathF.Max(logAverage * compensationExposure, 1e-6f), compensationWhitePointSquared);
        var outputMidGray = this.ForceToneMappingCore
            ? MathF.Max(this.settings.OutputMidGray, 0.33f)
            : this.settings.OutputMidGray;
        var brightnessCompensation = this.settings.AutoBrightnessCompensation || this.ForceToneMappingCore
            ? ComputeBrightnessCompensation(outputMidGray, mappedAverage)
            : 1f;
        var saturationRanges = this.settings.GetSaturationColorRanges();

        var exposureV = Vector256.Create(exposure);
        var whitePointSquaredV = Vector256.Create(adjustedWhitePointSquared);
        var brightnessCompensationV = Vector256.Create(brightnessCompensation);
        var contrastV = Vector256.Create(adaptiveContrast);
        var brightnessV = Vector256.Create(MathF.Max(this.settings.Brightness, 0f));
        var halfV = Vector256.Create(0.5f);
        var oneV = Vector256.Create(1f);
        var epsV = Vector256.Create(1e-6f);
        var adaptiveSaturationV = Vector256.Create(adaptiveSaturation);
        var satBaseV = Vector256.Create(adaptiveSaturation - 1f);

        for (var i = 0; i < pixels[0].Length; i++)
        {
            var r = pixels[0][i];
            var g = pixels[1][i];
            var b = pixels[2][i];
            var sourceR = r;
            var sourceG = g;
            var sourceB = b;

            var l = Avx.Add(Avx.Add(Avx.Multiply(r, ToneMapperSIMDHelper.Rw), Avx.Multiply(g, ToneMapperSIMDHelper.Gw)), Avx.Multiply(b, ToneMapperSIMDHelper.Bw));
            l = Avx.Max(l, epsV);

            var exposed = Avx.Multiply(l, exposureV);
            var shoulder = Avx.Add(oneV, Avx.Divide(exposed, whitePointSquaredV));
            var mappedLum = Avx.Divide(Avx.Multiply(exposed, shoulder), Avx.Add(oneV, exposed));
            mappedLum = Avx.Multiply(mappedLum, brightnessCompensationV);
            mappedLum = Avx.Multiply(mappedLum, ComputeToneBoost(mappedLum, this.settings.ShadowsBoost, this.settings.MidtonesBoost, this.settings.HighlightsBoost));
            mappedLum = ToneMapperSIMDHelper.Clamp01(Avx.Add(Avx.Multiply(Avx.Subtract(mappedLum, halfV), contrastV), halfV));
            mappedLum = ToneMapperSIMDHelper.Clamp01(Avx.Multiply(mappedLum, brightnessV));

            var scale = Avx.Divide(mappedLum, l);
            r = Avx.Multiply(r, scale);
            g = Avx.Multiply(g, scale);
            b = Avx.Multiply(b, scale);

            var compressed = Avx.Divide(Avx.Subtract(exposed, Avx.Divide(exposed, Avx.Add(oneV, exposed))), Avx.Add(exposed, epsV));
            compressed = ToneMapperSIMDHelper.Clamp01(compressed);
            var sat = adaptiveSaturation <= 1f
                ? adaptiveSaturationV
                : Avx.Add(oneV, Avx.Multiply(satBaseV, Avx.Subtract(oneV, compressed)));
            sat = ApplyVibrance(sat, r, g, b);
            sat = ApplySaturationRanges(sat, sourceR, sourceG, sourceB, saturationRanges);

            r = Avx.Add(mappedLum, Avx.Multiply(Avx.Subtract(r, mappedLum), sat));
            g = Avx.Add(mappedLum, Avx.Multiply(Avx.Subtract(g, mappedLum), sat));
            b = Avx.Add(mappedLum, Avx.Multiply(Avx.Subtract(b, mappedLum), sat));

            pixels[0][i] = ToneMapperSIMDHelper.Clamp01(r);
            pixels[1][i] = ToneMapperSIMDHelper.Clamp01(g);
            pixels[2][i] = ToneMapperSIMDHelper.Clamp01(b);
        }

        ApplyWhiteBalanceIfEnabled(pixels, width, height);
        ApplyGamma(pixels);
    }

    private void ApplyWhiteBalanceIfEnabled(Vector256<float>[][] pixels, int width, int height)
    {
        if (this.settings.WhiteBalanceReferenceType == WhiteBalanceReferenceType.None)
        {
            return;
        }

        this.whiteBalancer.ApplyInPlace(
            pixels,
            width,
            height,
            this.settings.WhiteBalanceReferenceType,
            this.settings.WhiteBalanceReferenceColor);
    }

    private void ApplyGamma(Vector256<float>[][] pixels)
    {
        var gamma = MathF.Max(this.settings.Gamma, 0.1f);
        if (MathF.Abs(gamma - 1f) <= 1e-3f)
        {
            return;
        }

        var invGammaV = Vector256.Create(1f / gamma);
        for (var ch = 0; ch < 3; ch++)
        {
            for (var i = 0; i < pixels[ch].Length; i++)
            {
                pixels[ch][i] = ToneMapperSIMDHelper.Clamp01(ToneMapperSIMDHelper.Pow(pixels[ch][i], invGammaV));
            }
        }
    }

    private static float CompressScalar(float exposedLum, float whitePointSquared)
    {
        return (exposedLum * (1f + (exposedLum / whitePointSquared))) / (1f + exposedLum);
    }

    private static float ComputeBrightnessCompensation(float outputMidGray, float currentMidGray)
    {
        return Math.Clamp(MathF.Max(outputMidGray, 0.01f) / MathF.Max(currentMidGray, 1e-6f), 0.1f, 4f);
    }

    private static Vector256<float> ComputeToneBoost(Vector256<float> value, float shadowsBoost, float midtonesBoost, float highlightsBoost)
    {
        var half = Vector256.Create(0.5f);
        var one = Vector256.Create(1f);
        var two = Vector256.Create(2f);
        var shadows = ToneMapperSIMDHelper.Clamp01(Avx.Divide(Avx.Subtract(half, value), half));
        var highlights = ToneMapperSIMDHelper.Clamp01(Avx.Divide(Avx.Subtract(value, half), half));
        var midtones = ToneMapperSIMDHelper.Clamp01(Avx.Subtract(one, Abs(Avx.Multiply(Avx.Subtract(value, half), two))));
        var adjustedShadows = 1f + ((shadowsBoost - 1f) * ToneBoostSensitivity);
        var adjustedMidtones = 1f + ((midtonesBoost - 1f) * ToneBoostSensitivity);
        var adjustedHighlights = 1f + ((highlightsBoost - 1f) * ToneBoostSensitivity);
        return Avx.Add(
            Avx.Add(Avx.Multiply(shadows, Vector256.Create(adjustedShadows)), Avx.Multiply(midtones, Vector256.Create(adjustedMidtones))),
            Avx.Multiply(highlights, Vector256.Create(adjustedHighlights)));
    }

    private static Vector256<float> ApplySaturationRanges(Vector256<float> baseSat, Vector256<float> r, Vector256<float> g, Vector256<float> b, SaturationColorRange[] ranges)
    {
        if (ranges.Length == 0)
        {
            return baseSat;
        }

        Span<float> sat = stackalloc float[Vector256<float>.Count];
        Span<float> rs = stackalloc float[Vector256<float>.Count];
        Span<float> gs = stackalloc float[Vector256<float>.Count];
        Span<float> bs = stackalloc float[Vector256<float>.Count];
        for (var lane = 0; lane < Vector256<float>.Count; lane++)
        {
            sat[lane] = baseSat[lane];
            rs[lane] = r[lane];
            gs[lane] = g[lane];
            bs[lane] = b[lane];
        }

        for (var lane = 0; lane < Vector256<float>.Count; lane++)
        {
            RgbToHsv(rs[lane], gs[lane], bs[lane], out var hue, out var saturation, out var value);
            for (var i = 0; i < ranges.Length; i++)
            {
                var strength = ComputeRangeStrength(ranges[i], hue, saturation, value);
                if (strength > 0f)
                {
                    sat[lane] += SaturationAdjustmentToMultiplierDelta(ranges[i].SaturationMultiplier) * strength;
                }
            }

            sat[lane] = MathF.Max(0f, sat[lane]);
        }

        return Vector256.Create(sat[0], sat[1], sat[2], sat[3], sat[4], sat[5], sat[6], sat[7]);
    }

    private static Vector256<float> ApplyVibrance(Vector256<float> baseSat, Vector256<float> r, Vector256<float> g, Vector256<float> b)
    {
        Span<float> sat = stackalloc float[Vector256<float>.Count];
        for (var lane = 0; lane < Vector256<float>.Count; lane++)
        {
            sat[lane] = baseSat[lane];
            if (sat[lane] <= 1f)
            {
                continue;
            }

            RgbToHsv(r[lane], g[lane], b[lane], out _, out var saturation, out _);
            sat[lane] = 1f + ((sat[lane] - 1f) * Math.Clamp(1f - saturation, 0f, 1f));
        }

        return Vector256.Create(sat[0], sat[1], sat[2], sat[3], sat[4], sat[5], sat[6], sat[7]);
    }

    private static float SaturationAdjustmentToMultiplierDelta(float adjustment)
    {
        var value = Math.Clamp(adjustment, -100f, 100f);
        return value <= 0f
            ? value / 100f
            : value / 50f;
    }

    private static float ComputeRangeStrength(SaturationColorRange range, float hue, float saturation, float value)
    {
        var strength = HueRangeStrength(hue, range.HueMin, range.HueMax);
        strength = MathF.Min(strength, LinearRangeStrength(saturation, range.SaturationMin, range.SaturationMax, 0f, 1f));
        strength = MathF.Min(strength, LinearRangeStrength(value, range.ValueMin, range.ValueMax, 0f, 1f));
        return strength;
    }

    private static float LinearRangeStrength(float value, float min, float max, float domainMin, float domainMax)
    {
        min = Math.Clamp(min, domainMin, domainMax);
        max = Math.Clamp(max, domainMin, domainMax);
        if (max < min)
        {
            (min, max) = (max, min);
        }

        var width = max - min;
        var domainWidth = domainMax - domainMin;
        if (width >= domainWidth - 1e-6f)
        {
            return 1f;
        }

        if (width <= 1e-6f)
        {
            return MathF.Abs(value - min) <= 1e-6f ? 1f : 0f;
        }

        if (value < min || value > max)
        {
            return 0f;
        }

        var distanceToEdge = MathF.Min(value - min, max - value);
        return Math.Clamp((distanceToEdge * 2f) / width, 0f, 1f);
    }

    private static float HueRangeStrength(float hue, float min, float max)
    {
        if (MathF.Abs(max - min) >= 360f - 1e-6f)
        {
            return 1f;
        }

        hue = NormalizeHue(hue);
        min = NormalizeHue(min);
        max = NormalizeHue(max);
        var width = max >= min
            ? max - min
            : (360f - min) + max;
        if (width <= 1e-6f)
        {
            return MathF.Min(NormalizeHue(hue - min), NormalizeHue(min - hue)) <= 1e-6f ? 1f : 0f;
        }

        var offset = NormalizeHue(hue - min);
        if (offset > width)
        {
            return 0f;
        }

        var distanceToEdge = MathF.Min(offset, width - offset);
        return Math.Clamp((distanceToEdge * 2f) / width, 0f, 1f);
    }

    private static float NormalizeHue(float hue)
    {
        hue %= 360f;
        return hue < 0f ? hue + 360f : hue;
    }

    private static void RgbToHsv(float r, float g, float b, out float hue, out float saturation, out float value)
    {
        var max = MathF.Max(r, MathF.Max(g, b));
        var min = MathF.Min(r, MathF.Min(g, b));
        var delta = max - min;
        value = max;
        saturation = max <= 1e-6f ? 0f : (delta / max);

        if (delta <= 1e-6f)
        {
            hue = 0f;
            return;
        }

        if (max == r)
        {
            hue = 60f * (((g - b) / delta) % 6f);
        }
        else if (max == g)
        {
            hue = 60f * (((b - r) / delta) + 2f);
        }
        else
        {
            hue = 60f * (((r - g) / delta) + 4f);
        }

        if (hue < 0f)
        {
            hue += 360f;
        }
    }

    private static Vector256<float> Abs(Vector256<float> value)
    {
        return Avx.AndNot(Vector256.Create(-0f), value);
    }

    private void ApplyLdrBypassAdjustments(Vector256<float>[][] pixels, float exposureCompensation)
    {
        var exposureCompensationV = Vector256.Create(exposureCompensation);
        var brightnessV = Vector256.Create(MathF.Max(this.settings.Brightness, 0f));
        var contrastV = Vector256.Create(MathF.Max(this.settings.Contrast, 0f));
        var halfV = Vector256.Create(0.5f);
        var oneV = Vector256.Create(1f);
        var epsV = Vector256.Create(1e-6f);
        var baseSatV = Vector256.Create(MathF.Max(0f, SaturationToMultiplier(this.settings.Saturation)));
        var saturationRanges = this.settings.GetSaturationColorRanges();

        for (var i = 0; i < pixels[0].Length; i++)
        {
            var r = pixels[0][i];
            var g = pixels[1][i];
            var b = pixels[2][i];
            var sourceR = r;
            var sourceG = g;
            var sourceB = b;

            var srcLum = Avx.Max(Avx.Add(Avx.Add(Avx.Multiply(r, ToneMapperSIMDHelper.Rw), Avx.Multiply(g, ToneMapperSIMDHelper.Gw)), Avx.Multiply(b, ToneMapperSIMDHelper.Bw)), epsV);
            var mappedLum = Avx.Multiply(srcLum, exposureCompensationV);
            mappedLum = ToneMapperSIMDHelper.Clamp01(Avx.Add(Avx.Multiply(Avx.Subtract(mappedLum, halfV), contrastV), halfV));
            mappedLum = ToneMapperSIMDHelper.Clamp01(Avx.Multiply(mappedLum, brightnessV));

            var scale = Avx.Divide(mappedLum, srcLum);
            r = Avx.Multiply(r, scale);
            g = Avx.Multiply(g, scale);
            b = Avx.Multiply(b, scale);

            var sat = saturationRanges.Length == 0
                ? ApplyVibrance(baseSatV, r, g, b)
                : ApplySaturationRanges(ApplyVibrance(baseSatV, r, g, b), sourceR, sourceG, sourceB, saturationRanges);

            r = Avx.Add(mappedLum, Avx.Multiply(Avx.Subtract(r, mappedLum), sat));
            g = Avx.Add(mappedLum, Avx.Multiply(Avx.Subtract(g, mappedLum), sat));
            b = Avx.Add(mappedLum, Avx.Multiply(Avx.Subtract(b, mappedLum), sat));

            pixels[0][i] = ToneMapperSIMDHelper.Clamp01(r);
            pixels[1][i] = ToneMapperSIMDHelper.Clamp01(g);
            pixels[2][i] = ToneMapperSIMDHelper.Clamp01(b);
        }
    }
}
