// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using System.Runtime.Intrinsics.X86;
using Image;
using Settings;

internal sealed class ContrastBalancerToneMapper : ToneMapper
{
    private const float Epsilon = 1e-6f;
    private readonly ContrastBalancerToneMapperSettings settings;

    public ContrastBalancerToneMapper(ContrastBalancerToneMapperSettings settings) : base(settings)
    {
        this.settings = settings;
    }

    protected override unsafe void ApplyInPlace(Image<Rgb> image, EffectiveToneMapperSettings effectiveSettings)
    {
        var saturationRanges = this.settings.GetSaturationColorRanges();
        if (Avx2.IsSupported && !this.settings.AutoAdjustEnabled && saturationRanges.Length == 0 && !this.ForceToneMappingCore)
        {
            var simd = new ContrastBalancerToneMapperSIMD(this.settings);
            this.ApplyUsingSimd(image, simd.ApplyCoreOnlyInPlace);
            return;
        }

        var count = image.Length;
        var sourcePixels = this.SourcePixelsBeforeProcessing;
        using var handle = new PinnedArray<Rgb>(image.Pixels);
        var pixels = handle.Pointer;

        var logSum = 0.0f;
        for (var i = 0; i < count; i++)
        {
            var lum = MathF.Max(pixels[i].Light(), Epsilon);
            logSum += MathF.Log(lum);
        }

        var avgLum = MathF.Exp(logSum / count);
        var strength = GetBalanceStrength(this.settings, effectiveSettings, this.ForceToneMappingCore);
        var toneCompression = MathF.Max(this.settings.ToneCompression, 1e-3f);
        var lightingEffect = Math.Max(0f, this.settings.LightingEffect);
        var luminanceScale = Math.Max(0f, this.settings.Luminance) * MathF.Pow(2f, effectiveSettings.ExposureEV);
        var blackClip = Math.Clamp(this.settings.BlackClip, 0f, 0.99f);
        var whiteClip = Math.Clamp(this.settings.WhiteClip, blackClip + 1e-3f, 4f);
        var invClipRange = 1f / (whiteClip - blackClip);
        var contrast = Math.Max(0f, effectiveSettings.Contrast);
        var saturation = Math.Max(0f, effectiveSettings.Saturation);
        var brightness = Math.Max(0f, effectiveSettings.Brightness);

        Parallel.For(0, count, i =>
        {
            var rgb = pixels[i];
            var sourceRgb = sourcePixels is null ? rgb : sourcePixels[i];
            var sourceLum = MathF.Max(rgb.Light(), Epsilon);
            var normalizedLum = (sourceLum * luminanceScale) / (sourceLum * luminanceScale + toneCompression);
            var adaptedLum = avgLum + ((normalizedLum - avgLum) * lightingEffect);
            adaptedLum = ((adaptedLum - blackClip) * invClipRange);
            adaptedLum = Math.Clamp(((adaptedLum - 0.5f) * contrast) + 0.5f, 0f, 1f);
            adaptedLum = Math.Clamp(adaptedLum * brightness, 0f, 1f);
            var mappedLum = sourceLum + ((adaptedLum - sourceLum) * strength);

            var scale = mappedLum / sourceLum;
            rgb *= scale;
            var adjustedSaturation = ApplySaturationRanges(saturation, sourceRgb, saturationRanges);
            rgb.Red = mappedLum + ((rgb.Red - mappedLum) * adjustedSaturation);
            rgb.Green = mappedLum + ((rgb.Green - mappedLum) * adjustedSaturation);
            rgb.Blue = mappedLum + ((rgb.Blue - mappedLum) * adjustedSaturation);

            rgb.Red = Math.Clamp(rgb.Red, 0f, 1f);
            rgb.Green = Math.Clamp(rgb.Green, 0f, 1f);
            rgb.Blue = Math.Clamp(rgb.Blue, 0f, 1f);
            pixels[i] = rgb;
        });

        ApplyGamma(pixels, count, effectiveSettings.Gamma);
    }

    protected override bool PreservesSourceBeforeProcessing => this.settings.GetSaturationColorRanges().Length != 0;

    private static float GetBalanceStrength(ContrastBalancerToneMapperSettings settings, EffectiveToneMapperSettings effectiveSettings, bool forceToneMappingCore)
    {
        return forceToneMappingCore || HasActiveBalanceControls(settings, effectiveSettings)
            ? Math.Clamp(settings.Strength, 0f, 1f)
            : 0f;
    }

    private static bool HasActiveBalanceControls(ContrastBalancerToneMapperSettings settings, EffectiveToneMapperSettings effectiveSettings)
    {
        return MathF.Abs(settings.ToneCompression - 1f) > Epsilon ||
               MathF.Abs(settings.LightingEffect - 1f) > Epsilon ||
               MathF.Abs(settings.Luminance - 1f) > Epsilon ||
               MathF.Abs(settings.WhiteClip - ClippedToneMapperSettings.NeutralWhiteClip) > Epsilon ||
               MathF.Abs(settings.BlackClip - ClippedToneMapperSettings.NeutralBlackClip) > Epsilon ||
               MathF.Abs(effectiveSettings.ExposureEV) > Epsilon ||
               MathF.Abs(effectiveSettings.Brightness - 1f) > Epsilon ||
               MathF.Abs(effectiveSettings.Contrast - 1f) > Epsilon;
    }

    private static unsafe void ApplyGamma(Rgb* pixels, long count, float gamma)
    {
        if (MathF.Abs(gamma - 1f) <= 1e-3f)
        {
            return;
        }

        var invGamma = 1f / MathF.Max(gamma, 0.1f);
        Parallel.For(0, count, i =>
        {
            var rgb = pixels[i];
            rgb.Red = Math.Clamp(MathF.Pow(rgb.Red, invGamma), 0f, 1f);
            rgb.Green = Math.Clamp(MathF.Pow(rgb.Green, invGamma), 0f, 1f);
            rgb.Blue = Math.Clamp(MathF.Pow(rgb.Blue, invGamma), 0f, 1f);
            pixels[i] = rgb;
        });
    }

    private static float ApplySaturationRanges(float baseSaturation, Rgb rgb, SaturationColorRange[] ranges)
    {
        if (ranges.Length == 0)
        {
            return baseSaturation;
        }

        RgbToHsv(rgb, out var hue, out var saturation, out var value);
        var adjusted = baseSaturation;
        for (var i = 0; i < ranges.Length; i++)
        {
            var strength = ComputeRangeStrength(ranges[i], hue, saturation, value);
            if (strength > 0f)
            {
                adjusted += SaturationAdjustmentToMultiplierDelta(ranges[i].SaturationMultiplier) * strength;
            }
        }

        return MathF.Max(0f, adjusted);
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
        if (width >= domainWidth - Epsilon)
        {
            return 1f;
        }

        if (width <= Epsilon)
        {
            return MathF.Abs(value - min) <= Epsilon ? 1f : 0f;
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
        if (MathF.Abs(max - min) >= 360f - Epsilon)
        {
            return 1f;
        }

        hue = NormalizeHue(hue);
        min = NormalizeHue(min);
        max = NormalizeHue(max);
        var width = max >= min
            ? max - min
            : (360f - min) + max;
        if (width <= Epsilon)
        {
            return MathF.Min(NormalizeHue(hue - min), NormalizeHue(min - hue)) <= Epsilon ? 1f : 0f;
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

    private static void RgbToHsv(Rgb rgb, out float hue, out float saturation, out float value)
    {
        var r = rgb.Red;
        var g = rgb.Green;
        var b = rgb.Blue;
        var max = MathF.Max(r, MathF.Max(g, b));
        var min = MathF.Min(r, MathF.Min(g, b));
        var delta = max - min;
        value = max;
        saturation = max <= Epsilon ? 0f : delta / max;

        if (delta <= Epsilon)
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
}
