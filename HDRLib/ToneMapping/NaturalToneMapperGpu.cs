// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using Adjust;
using HDRLib.Gpu;
using HDRLib.Image;
using HDRLib.ToneMapping.Settings;
using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;

internal sealed class NaturalToneMapperGpu : ToneMapperGpu
{
    private const int LuminanceStatsChunkSize = 1024;

    private readonly Accelerator accelerator;
    private readonly NaturalToneMapperSettings settings;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>> extractLuminanceKernel;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>, int> luminanceStatsKernel;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float, float, float, float> applyKernel;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float, float, float, float, ArrayView1D<float, Stride1D.Dense>, int> applyKernelWithRanges;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float, float> applyLdrBypassKernel;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float, float, ArrayView1D<float, Stride1D.Dense>, int> applyLdrBypassKernelWithRanges;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float> applyGammaKernel;

    public NaturalToneMapperGpu(GpuContext context, NaturalToneMapperSettings settings) : base(context, settings)
    {
        this.accelerator = context.Accelerator;
        this.settings = settings;
        this.extractLuminanceKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>>(ExtractLuminanceKernel);
        this.luminanceStatsKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>, int>(LuminanceStatsKernel);
        this.applyKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float, float, float, float>(ApplyKernel);
        this.applyKernelWithRanges = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float, float, float, float, ArrayView1D<float, Stride1D.Dense>, int>(ApplyKernelWithRanges);
        this.applyLdrBypassKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float, float>(ApplyLdrBypassKernel);
        this.applyLdrBypassKernelWithRanges = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float, float, ArrayView1D<float, Stride1D.Dense>, int>(ApplyLdrBypassKernelWithRanges);
        this.applyGammaKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float>(ApplyGammaKernel);
    }

    protected override bool NormalizesInputRange => false;

    protected override bool PreservesSourceBeforeProcessing => true;

    protected override void ApplyInPlace(ArrayView1D<Rgb, Stride1D.Dense> gpuPixels, EffectiveToneMapperSettings effectiveSettings)
    {
        var accelerator = this.accelerator;
        var pixelCount = (int)gpuPixels.Length;
        var exposureCompensation = MathF.Pow(2f, effectiveSettings.ExposureEV);
        var brightness = effectiveSettings.Brightness;
        var contrast = effectiveSettings.Contrast;
        var baseSaturation = MathF.Max(0f, effectiveSettings.Saturation);
        var gamma = effectiveSettings.Gamma;
        var saturationRanges = this.settings.GetSaturationColorRanges();

        var logSum = 0.0;
        var maxLum = 0f;
        var whiteLum = 0f;
        if (this.settings.WhitePointPercentile >= 1f)
        {
            var chunkCount = (pixelCount + LuminanceStatsChunkSize - 1) / LuminanceStatsChunkSize;
            using var statsBuffer = accelerator.Allocate1D<Rgb>(chunkCount);
            this.luminanceStatsKernel(chunkCount, gpuPixels, statsBuffer.View, LuminanceStatsChunkSize);

            var stats = statsBuffer.GetAsArray1D();
            for (var i = 0; i < stats.Length; i++)
            {
                logSum += stats[i].Red;
                if (stats[i].Green > maxLum)
                {
                    maxLum = stats[i].Green;
                }
            }

            whiteLum = maxLum;
        }
        else
        {
            using var luminanceBuffer = accelerator.Allocate1D<float>(pixelCount);
            this.extractLuminanceKernel(pixelCount, gpuPixels, luminanceBuffer.View);

            var luminance = luminanceBuffer.GetAsArray1D();
            for (var i = 0; i < luminance.Length; i++)
            {
                var l = MathF.Max(luminance[i], 1e-6f);
                logSum += MathF.Log(l);
                if (l > maxLum)
                {
                    maxLum = l;
                }
            }

            Array.Sort(luminance);
            whiteLum = Percentile(luminance, this.settings.WhitePointPercentile);
        }

        var logAverage = MathF.Exp((float)(logSum / pixelCount));
        if (!this.ForceToneMappingCore &&
            this.settings.BypassToneCompressionForLdr &&
            whiteLum <= this.settings.LdrBypassWhitePointThreshold &&
            maxLum <= this.settings.LdrBypassWhitePointThreshold)
        {
            var ldrBrightnessCompensation = this.settings.AutoBrightnessCompensation
                ? ComputeBrightnessCompensation(this.settings.OutputMidGray, logAverage * exposureCompensation)
                : 1f;
            var ldrExposure = exposureCompensation * ldrBrightnessCompensation;
            var ldrBrightness = MathF.Max(brightness, 0f);
            var ldrContrast = MathF.Max(contrast, 0f);
            var ldrSaturation = baseSaturation;
            if (saturationRanges.Length == 0)
            {
                this.applyLdrBypassKernel(pixelCount, gpuPixels, ldrExposure, ldrBrightness, ldrContrast, ldrSaturation);
            }
            else
            {
                var packedRanges = PackSaturationRanges(saturationRanges);
                using var ranges = accelerator.Allocate1D(packedRanges);
                this.applyLdrBypassKernelWithRanges(pixelCount, gpuPixels, this.SourcePixelsBeforeProcessing, ldrExposure, ldrBrightness, ldrContrast, ldrSaturation, ranges.View, saturationRanges.Length);
            }

            accelerator.Synchronize();
            this.ApplyGamma(gpuPixels, pixelCount, gamma);
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
        var adaptiveContrast = 1f + ((contrast - 1f) * adaptiveContrastFactor);
        var adaptiveSaturation = baseSaturation <= 1f
            ? baseSaturation
            : 1f + ((baseSaturation - 1f) * (0.4f + (0.6f * dynamicRangeFactor)));
        var compensationWhitePoint = MathF.Max(whiteLum * compensationExposure, 1e-3f);
        var compensationWhitePointSquared = (compensationWhitePoint * compensationWhitePoint) * tonalRangeCompression;
        var mappedAverage = Compress(MathF.Max(logAverage * compensationExposure, 1e-6f), compensationWhitePointSquared);
        var outputMidGray = this.ForceToneMappingCore
            ? MathF.Max(this.settings.OutputMidGray, 0.33f)
            : this.settings.OutputMidGray;
        var brightnessCompensation = this.settings.AutoBrightnessCompensation || this.ForceToneMappingCore
            ? ComputeBrightnessCompensation(outputMidGray, mappedAverage)
            : 1f;

        if (saturationRanges.Length == 0)
        {
            this.applyKernel(pixelCount, gpuPixels, exposure, adjustedWhitePointSquared, brightnessCompensation, adaptiveContrast, MathF.Max(brightness, 0f), adaptiveSaturation);
        }
        else
        {
            var packedRanges = PackSaturationRanges(saturationRanges);
            using var ranges = accelerator.Allocate1D(packedRanges);
            this.applyKernelWithRanges(
                pixelCount,
                gpuPixels,
                this.SourcePixelsBeforeProcessing,
                exposure,
                adjustedWhitePointSquared,
                brightnessCompensation,
                adaptiveContrast,
                MathF.Max(brightness, 0f),
                adaptiveSaturation,
                ranges.View,
                saturationRanges.Length);
        }

        accelerator.Synchronize();
        this.ApplyGamma(gpuPixels, pixelCount, gamma);
    }

    private static float[] PackSaturationRanges(SaturationColorRange[] ranges)
    {
        var packed = new float[ranges.Length * 7];
        for (var i = 0; i < ranges.Length; i++)
        {
            var offset = i * 7;
            packed[offset] = ranges[i].HueMin;
            packed[offset + 1] = ranges[i].HueMax;
            packed[offset + 2] = ranges[i].SaturationMin;
            packed[offset + 3] = ranges[i].SaturationMax;
            packed[offset + 4] = ranges[i].ValueMin;
            packed[offset + 5] = ranges[i].ValueMax;
            packed[offset + 6] = ranges[i].SaturationMultiplier;
        }

        return packed;
    }

    private void ApplyGamma(ArrayView1D<Rgb, Stride1D.Dense> gpuPixels, int pixelCount, float gamma)
    {
        gamma = MathF.Max(gamma, 0.1f);
        if (MathF.Abs(gamma - 1f) <= 1e-3f)
        {
            return;
        }

        this.applyGammaKernel(pixelCount, gpuPixels, 1f / gamma);
        this.accelerator.Synchronize();
    }

    private static void ExtractLuminanceKernel(Index1D index, ArrayView1D<Rgb, Stride1D.Dense> pixels, ArrayView1D<float, Stride1D.Dense> luminance)
    {
        luminance[index] = pixels[index].Light();
    }

    private static void LuminanceStatsKernel(Index1D index, ArrayView1D<Rgb, Stride1D.Dense> pixels, ArrayView1D<Rgb, Stride1D.Dense> stats, int chunkSize)
    {
        var start = (int)index * chunkSize;
        var end = XMath.Min(start + chunkSize, (int)pixels.Length);
        var logSum = 0f;
        var maxLum = 0f;

        for (var i = start; i < end; i++)
        {
            var lum = XMath.Max(pixels[i].Light(), 1e-6f);
            logSum += GpuHelper.Log(lum);
            maxLum = XMath.Max(maxLum, lum);
        }

        stats[index] = new Rgb(logSum, maxLum, 0f);
    }

    private static void ApplyKernel(
        Index1D index,
        ArrayView1D<Rgb, Stride1D.Dense> pixels,
        float exposure,
        float whitePointSquared,
        float brightnessCompensation,
        float adaptiveContrast,
        float brightness,
        float adaptiveSaturation)
    {
        var rgb = pixels[index];
        var lum = XMath.Max(rgb.Light(), 1e-6f);

        var exposedLum = lum * exposure;
        var mappedLum = Compress(exposedLum, whitePointSquared) * brightnessCompensation;
        mappedLum = XMath.Clamp(((mappedLum - 0.5f) * adaptiveContrast) + 0.5f, 0f, 1f);
        mappedLum = XMath.Clamp(mappedLum * brightness, 0f, 1f);

        var scale = mappedLum / lum;
        rgb.Red *= scale;
        rgb.Green *= scale;
        rgb.Blue *= scale;

        var compressed = (exposedLum - (exposedLum / (1f + exposedLum))) / (exposedLum + 1e-6f);
        compressed = XMath.Clamp(compressed, 0f, 1f);
        var sat = adaptiveSaturation <= 1f
            ? adaptiveSaturation
            : 1f + ((adaptiveSaturation - 1f) * (1f - compressed));
        sat = ApplyVibrance(sat, rgb);

        rgb.Red = mappedLum + ((rgb.Red - mappedLum) * sat);
        rgb.Green = mappedLum + ((rgb.Green - mappedLum) * sat);
        rgb.Blue = mappedLum + ((rgb.Blue - mappedLum) * sat);

        rgb.Red = XMath.Clamp(rgb.Red, 0f, 1f);
        rgb.Green = XMath.Clamp(rgb.Green, 0f, 1f);
        rgb.Blue = XMath.Clamp(rgb.Blue, 0f, 1f);

        pixels[index] = rgb;
    }

    private static void ApplyKernelWithRanges(
        Index1D index,
        ArrayView1D<Rgb, Stride1D.Dense> pixels,
        ArrayView1D<Rgb, Stride1D.Dense> sourcePixels,
        float exposure,
        float whitePointSquared,
        float brightnessCompensation,
        float adaptiveContrast,
        float brightness,
        float adaptiveSaturation,
        ArrayView1D<float, Stride1D.Dense> ranges,
        int rangeCount)
    {
        var rgb = pixels[index];
        var sourceRgb = sourcePixels[index];
        var lum = XMath.Max(rgb.Light(), 1e-6f);

        var exposedLum = lum * exposure;
        var mappedLum = Compress(exposedLum, whitePointSquared) * brightnessCompensation;
        mappedLum = XMath.Clamp(((mappedLum - 0.5f) * adaptiveContrast) + 0.5f, 0f, 1f);
        mappedLum = XMath.Clamp(mappedLum * brightness, 0f, 1f);

        var scale = mappedLum / lum;
        rgb.Red *= scale;
        rgb.Green *= scale;
        rgb.Blue *= scale;

        var compressed = (exposedLum - (exposedLum / (1f + exposedLum))) / (exposedLum + 1e-6f);
        compressed = XMath.Clamp(compressed, 0f, 1f);
        var sat = adaptiveSaturation <= 1f
            ? adaptiveSaturation
            : 1f + ((adaptiveSaturation - 1f) * (1f - compressed));
        sat = ApplyVibrance(sat, rgb);
        sat = ApplySaturationRanges(sat, sourceRgb, ranges, rangeCount);

        rgb.Red = mappedLum + ((rgb.Red - mappedLum) * sat);
        rgb.Green = mappedLum + ((rgb.Green - mappedLum) * sat);
        rgb.Blue = mappedLum + ((rgb.Blue - mappedLum) * sat);

        rgb.Red = XMath.Clamp(rgb.Red, 0f, 1f);
        rgb.Green = XMath.Clamp(rgb.Green, 0f, 1f);
        rgb.Blue = XMath.Clamp(rgb.Blue, 0f, 1f);
        pixels[index] = rgb;
    }

    private static void ApplyGammaKernel(Index1D index, ArrayView1D<Rgb, Stride1D.Dense> pixels, float invGamma)
    {
        var rgb = pixels[index];
        rgb.Red = XMath.Clamp(GpuHelper.Pow(rgb.Red, invGamma), 0f, 1f);
        rgb.Green = XMath.Clamp(GpuHelper.Pow(rgb.Green, invGamma), 0f, 1f);
        rgb.Blue = XMath.Clamp(GpuHelper.Pow(rgb.Blue, invGamma), 0f, 1f);
        pixels[index] = rgb;
    }

    private static void ApplyLdrBypassKernel(
        Index1D index,
        ArrayView1D<Rgb, Stride1D.Dense> pixels,
        float exposure,
        float brightness,
        float contrast,
        float saturation)
    {
        var rgb = pixels[index];
        var srcLum = XMath.Max(rgb.Light(), 1e-6f);
        var mappedLum = XMath.Clamp(((srcLum * exposure - 0.5f) * contrast) + 0.5f, 0f, 1f);
        mappedLum = XMath.Clamp(mappedLum * brightness, 0f, 1f);

        var scale = mappedLum / srcLum;
        rgb.Red *= scale;
        rgb.Green *= scale;
        rgb.Blue *= scale;

        var adjustedSaturation = ApplyVibrance(saturation, rgb);
        rgb.Red = mappedLum + ((rgb.Red - mappedLum) * adjustedSaturation);
        rgb.Green = mappedLum + ((rgb.Green - mappedLum) * adjustedSaturation);
        rgb.Blue = mappedLum + ((rgb.Blue - mappedLum) * adjustedSaturation);

        rgb.Red = XMath.Clamp(rgb.Red, 0f, 1f);
        rgb.Green = XMath.Clamp(rgb.Green, 0f, 1f);
        rgb.Blue = XMath.Clamp(rgb.Blue, 0f, 1f);
        pixels[index] = rgb;
    }

    private static void ApplyLdrBypassKernelWithRanges(
        Index1D index,
        ArrayView1D<Rgb, Stride1D.Dense> pixels,
        ArrayView1D<Rgb, Stride1D.Dense> sourcePixels,
        float exposure,
        float brightness,
        float contrast,
        float saturation,
        ArrayView1D<float, Stride1D.Dense> ranges,
        int rangeCount)
    {
        var rgb = pixels[index];
        var sourceRgb = sourcePixels[index];
        var srcLum = XMath.Max(rgb.Light(), 1e-6f);
        var mappedLum = XMath.Clamp(((srcLum * exposure - 0.5f) * contrast) + 0.5f, 0f, 1f);
        mappedLum = XMath.Clamp(mappedLum * brightness, 0f, 1f);

        var scale = mappedLum / srcLum;
        rgb.Red *= scale;
        rgb.Green *= scale;
        rgb.Blue *= scale;

        var adjustedSaturation = ApplyVibrance(saturation, rgb);
        adjustedSaturation = ApplySaturationRanges(adjustedSaturation, sourceRgb, ranges, rangeCount);
        rgb.Red = mappedLum + ((rgb.Red - mappedLum) * adjustedSaturation);
        rgb.Green = mappedLum + ((rgb.Green - mappedLum) * adjustedSaturation);
        rgb.Blue = mappedLum + ((rgb.Blue - mappedLum) * adjustedSaturation);

        rgb.Red = XMath.Clamp(rgb.Red, 0f, 1f);
        rgb.Green = XMath.Clamp(rgb.Green, 0f, 1f);
        rgb.Blue = XMath.Clamp(rgb.Blue, 0f, 1f);
        pixels[index] = rgb;
    }

    private static float Compress(float exposedLum, float whitePointSquared)
    {
        return (exposedLum * (1f + (exposedLum / whitePointSquared))) / (1f + exposedLum);
    }

    private static float ComputeBrightnessCompensation(float outputMidGray, float currentMidGray)
    {
        return Math.Clamp(MathF.Max(outputMidGray, 0.01f) / MathF.Max(currentMidGray, 1e-6f), 0.1f, 4f);
    }

    private static float ApplySaturationRanges(float baseSaturation, Rgb rgb, ArrayView1D<float, Stride1D.Dense> ranges, int count)
    {
        RgbToHsv(rgb, out var hue, out var saturation, out var value);
        var adjusted = baseSaturation;
        for (var i = 0; i < count; i++)
        {
            var offset = i * 7;
            var strength = ComputeRangeStrength(ranges, offset, hue, saturation, value);
            if (strength > 0f)
            {
                adjusted += SaturationAdjustmentToMultiplierDelta(ranges[offset + 6]) * strength;
            }
        }

        return XMath.Max(0f, adjusted);
    }

    private static float ApplyVibrance(float baseSaturation, Rgb rgb)
    {
        if (baseSaturation <= 1f)
        {
            return baseSaturation;
        }

        RgbToHsv(rgb, out _, out var saturation, out _);
        return 1f + ((baseSaturation - 1f) * XMath.Clamp(1f - saturation, 0f, 1f));
    }

    private static float SaturationAdjustmentToMultiplierDelta(float adjustment)
    {
        var value = XMath.Clamp(adjustment, -100f, 100f);
        return value <= 0f
            ? value / 100f
            : value / 50f;
    }

    private static float ComputeRangeStrength(ArrayView1D<float, Stride1D.Dense> ranges, int offset, float hue, float saturation, float value)
    {
        var strength = HueRangeStrength(hue, ranges[offset], ranges[offset + 1]);
        strength = XMath.Min(strength, LinearRangeStrength(saturation, ranges[offset + 2], ranges[offset + 3], 0f, 1f));
        strength = XMath.Min(strength, LinearRangeStrength(value, ranges[offset + 4], ranges[offset + 5], 0f, 1f));
        return strength;
    }

    private static float LinearRangeStrength(float value, float min, float max, float domainMin, float domainMax)
    {
        min = XMath.Clamp(min, domainMin, domainMax);
        max = XMath.Clamp(max, domainMin, domainMax);
        if (max < min)
        {
            var oldMin = min;
            min = max;
            max = oldMin;
        }

        var width = max - min;
        var domainWidth = domainMax - domainMin;
        if (width >= domainWidth - 1e-6f)
        {
            return 1f;
        }

        if (width <= 1e-6f)
        {
            return XMath.Abs(value - min) <= 1e-6f ? 1f : 0f;
        }

        if (value < min || value > max)
        {
            return 0f;
        }

        var distanceToEdge = XMath.Min(value - min, max - value);
        return XMath.Clamp((distanceToEdge * 2f) / width, 0f, 1f);
    }

    private static float HueRangeStrength(float hue, float min, float max)
    {
        if (XMath.Abs(max - min) >= 360f - 1e-6f)
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
            return XMath.Min(NormalizeHue(hue - min), NormalizeHue(min - hue)) <= 1e-6f ? 1f : 0f;
        }

        var hueOffset = NormalizeHue(hue - min);
        if (hueOffset > width)
        {
            return 0f;
        }

        var distanceToEdge = XMath.Min(hueOffset, width - hueOffset);
        return XMath.Clamp((distanceToEdge * 2f) / width, 0f, 1f);
    }

    private static float NormalizeHue(float hue)
    {
        hue = GpuHelper.RemFloat(hue, 360f);
        return hue < 0f ? hue + 360f : hue;
    }

    private static void RgbToHsv(Rgb rgb, out float hue, out float saturation, out float value)
    {
        var r = rgb.Red;
        var g = rgb.Green;
        var b = rgb.Blue;
        var max = XMath.Max(r, XMath.Max(g, b));
        var min = XMath.Min(r, XMath.Min(g, b));
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
            hue = 60f * GpuHelper.RemFloat(((g - b) / delta) , 6f);
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
}
