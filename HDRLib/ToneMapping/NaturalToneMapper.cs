// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using System.Threading.Tasks;
using Image;
using Settings;

internal sealed class NaturalToneMapper : ToneMapper
{
    private const float Epsilon = 1e-6f;
    private readonly NaturalToneMapperSettings settings;

    public NaturalToneMapper(NaturalToneMapperSettings settings) : base(settings)
    {
        this.settings = settings;
    }

    protected override bool NormalizesInputRange => false;

    protected override bool PreservesSourceBeforeProcessing => true;

    protected override unsafe void ApplyInPlace(Image<Rgb> image, EffectiveToneMapperSettings effectiveSettings)
    {
        var pixelCount = image.Length;
        var sourcePixels = this.SourcePixelsBeforeProcessing;

        using var handle = new PinnedArray<Rgb>(image.Pixels);
        var pixels = handle.Pointer;

        var logSum = 0.0;
        var maxLum = 0f;
        var luminance = new float[pixelCount];
        for (var i = 0; i < pixelCount; i++)
        {
            var lum = MathF.Max(pixels[i].Light(), Epsilon);
            luminance[i] = lum;
            logSum += MathF.Log(lum);
            if (lum > maxLum)
            {
                maxLum = lum;
            }
        }

        Array.Sort(luminance);
        var logAverage = MathF.Exp((float)(logSum / pixelCount));
        var whiteLum = Percentile(luminance, this.settings.WhitePointPercentile);
        var exposureCompensation = MathF.Pow(2f, effectiveSettings.ExposureEV);
        if (this.settings.BypassToneCompressionForLdr &&
            whiteLum <= this.settings.LdrBypassWhitePointThreshold &&
            maxLum <= this.settings.LdrBypassWhitePointThreshold)
        {
            var ldrBrightnessCompensation = this.settings.AutoBrightnessCompensation
                ? ComputeBrightnessCompensation(this.settings.OutputMidGray, logAverage * exposureCompensation)
                : 1f;
            ApplyLdrBypassAdjustments(pixels, sourcePixels, (int)pixelCount, exposureCompensation * ldrBrightnessCompensation, effectiveSettings);
            ApplyGamma(pixels, (int)pixelCount, effectiveSettings.Gamma);
            return;
        }

        var compensationExposure = MathF.Max(this.settings.TargetGray, 0.01f) / MathF.Max(logAverage, Epsilon);
        var exposure = compensationExposure * exposureCompensation;

        var whitePoint = MathF.Max(whiteLum * compensationExposure, 1e-3f);
        var whitePointSquared = whitePoint * whitePoint;
        var tonalRangeCompression = MathF.Max(this.settings.TonalRangeCompression, 1e-3f);
        var adjustedWhitePointSquared = whitePointSquared * tonalRangeCompression;
        var dynamicRangeFactor = maxLum / (maxLum + logAverage + Epsilon);
        var contrastAdaptation = 1f;
        var adaptiveContrastFactor = (1f - contrastAdaptation) + (contrastAdaptation * (0.35f + (0.65f * dynamicRangeFactor)));
        var adaptiveContrast = 1f + ((effectiveSettings.Contrast - 1f) * adaptiveContrastFactor);
        var baseSaturation = MathF.Max(0f, effectiveSettings.Saturation);
        var adaptiveSaturation = baseSaturation <= 1f
            ? baseSaturation
            : 1f + ((baseSaturation - 1f) * (0.4f + (0.6f * dynamicRangeFactor)));
        var compensationWhitePoint = MathF.Max(whiteLum * compensationExposure, 1e-3f);
        var compensationWhitePointSquared = (compensationWhitePoint * compensationWhitePoint) * tonalRangeCompression;
        var mappedAverage = Compress(MathF.Max(logAverage * compensationExposure, Epsilon), compensationWhitePointSquared);
        var brightnessCompensation = this.settings.AutoBrightnessCompensation
            ? ComputeBrightnessCompensation(this.settings.OutputMidGray, mappedAverage)
            : 1f;
        var saturationRanges = this.settings.GetSaturationColorRanges();

        Parallel.For(0, pixelCount, i =>
        {
            var rgb = pixels[i];
            var sourceRgb = sourcePixels is null ? rgb : sourcePixels[i];
            var lum = MathF.Max(rgb.Light(), Epsilon);

            var exposedLum = lum * exposure;
            var mappedLum = Compress(exposedLum, adjustedWhitePointSquared) * brightnessCompensation;
            mappedLum = Math.Clamp(((mappedLum - 0.5f) * adaptiveContrast) + 0.5f, 0f, 1f);
            mappedLum = Math.Clamp(mappedLum * effectiveSettings.Brightness, 0f, 1f);

            var scale = mappedLum / lum;
            var mapped = rgb * scale;

            var highlightCompression = (exposedLum - (exposedLum / (1f + exposedLum))) / (exposedLum + Epsilon);
            var sat = adaptiveSaturation <= 1f
                ? adaptiveSaturation
                : 1f + ((adaptiveSaturation - 1f) * (1f - Math.Clamp(highlightCompression, 0f, 1f)));
            sat = ApplyVibrance(sat, mapped);
            sat = ApplySaturationRanges(sat, sourceRgb, saturationRanges);

            mapped.Red = mappedLum + ((mapped.Red - mappedLum) * sat);
            mapped.Green = mappedLum + ((mapped.Green - mappedLum) * sat);
            mapped.Blue = mappedLum + ((mapped.Blue - mappedLum) * sat);

            mapped.Red = Math.Clamp(mapped.Red, 0f, 1f);
            mapped.Green = Math.Clamp(mapped.Green, 0f, 1f);
            mapped.Blue = Math.Clamp(mapped.Blue, 0f, 1f);

            pixels[i] = mapped;
        });

        ApplyGamma(pixels, (int)pixelCount, effectiveSettings.Gamma);
    }

    private static float Compress(float exposedLum, float whitePointSquared)
    {
        var shoulder = 1f + (exposedLum / whitePointSquared);
        return (exposedLum * shoulder) / (1f + exposedLum);
    }

    private static float ComputeBrightnessCompensation(float outputMidGray, float currentMidGray)
    {
        return Math.Clamp(MathF.Max(outputMidGray, 0.01f) / MathF.Max(currentMidGray, Epsilon), 0.1f, 4f);
    }

    private static float Percentile(float[] sortedArray, float percentile)
    {
        if (sortedArray.Length == 0)
        {
            return 0f;
        }

        var p = Math.Clamp(percentile, 0f, 1f);
        var pos = p * (sortedArray.Length - 1);
        var index = (int)pos;
        var frac = pos - index;

        if (index >= sortedArray.Length - 1)
        {
            return sortedArray[^1];
        }

        return (sortedArray[index] * (1f - frac)) + (sortedArray[index + 1] * frac);
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

    private static float ApplyVibrance(float baseSaturation, Rgb rgb)
    {
        if (baseSaturation <= 1f)
        {
            return baseSaturation;
        }

        RgbToHsv(rgb, out _, out var saturation, out _);
        return 1f + ((baseSaturation - 1f) * Math.Clamp(1f - saturation, 0f, 1f));
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
        saturation = max <= Epsilon ? 0f : (delta / max);

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

    private unsafe void ApplyGamma(Rgb* pixels, int pixelCount, float gammaValue)
    {
        var gamma = MathF.Max(gammaValue, 0.1f);
        if (MathF.Abs(gamma - 1f) <= 1e-3f)
        {
            return;
        }

        var invGamma = 1f / gamma;
        Parallel.For(0, pixelCount, i =>
        {
            var mapped = pixels[i];
            mapped.Red = Math.Clamp(MathF.Pow(mapped.Red, invGamma), 0f, 1f);
            mapped.Green = Math.Clamp(MathF.Pow(mapped.Green, invGamma), 0f, 1f);
            mapped.Blue = Math.Clamp(MathF.Pow(mapped.Blue, invGamma), 0f, 1f);
            pixels[i] = mapped;
        });
    }

    private unsafe void ApplyLdrBypassAdjustments(Rgb* pixels, Rgb[]? sourcePixels, int pixelCount, float exposureCompensation, EffectiveToneMapperSettings effectiveSettings)
    {
        var brightness = MathF.Max(effectiveSettings.Brightness, 0f);
        var contrast = MathF.Max(effectiveSettings.Contrast, 0f);
        var saturation = MathF.Max(0f, effectiveSettings.Saturation);
        var saturationRanges = this.settings.GetSaturationColorRanges();

        Parallel.For(0, pixelCount, i =>
        {
            var mapped = pixels[i];
            var sourceRgb = sourcePixels is null ? mapped : sourcePixels[i];
            var lum = MathF.Max(mapped.Light() * exposureCompensation, Epsilon);
            lum = Math.Clamp(((lum - 0.5f) * contrast) + 0.5f, 0f, 1f);
            lum = Math.Clamp(lum * brightness, 0f, 1f);

            var scale = lum / MathF.Max(mapped.Light(), Epsilon);
            mapped *= scale;

            var sat = ApplyVibrance(saturation, mapped);
            sat = ApplySaturationRanges(sat, sourceRgb, saturationRanges);
            mapped.Red = lum + ((mapped.Red - lum) * sat);
            mapped.Green = lum + ((mapped.Green - lum) * sat);
            mapped.Blue = lum + ((mapped.Blue - lum) * sat);

            mapped.Red = Math.Clamp(mapped.Red, 0f, 1f);
            mapped.Green = Math.Clamp(mapped.Green, 0f, 1f);
            mapped.Blue = Math.Clamp(mapped.Blue, 0f, 1f);

            pixels[i] = mapped;
        });
    }
}
