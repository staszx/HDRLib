// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using Adjust;
using HDRLib.Gpu;
using HDRLib.Image;
using HDRLib.ToneMapping.Settings;
using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;

internal sealed class ContrastBalancerToneMapperGpu : ToneMapperGpu
{
    private const float Epsilon = 1e-6f;
    private readonly Accelerator accelerator;
    private readonly ContrastBalancerToneMapperSettings settings;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>> logSumKernel;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float, float, float, float, float, float, float, float> applyKernel;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float, float, float, float, float, float, float, float, ArrayView1D<float, Stride1D.Dense>, int> applyKernelWithRanges;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float> applyGammaKernel;

    public ContrastBalancerToneMapperGpu(GpuContext context, ContrastBalancerToneMapperSettings settings) : base(context, settings)
    {
        this.accelerator = context.Accelerator;
        this.settings = settings;
        this.logSumKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>>(LogSumKernel);
        this.applyKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float, float, float, float, float, float, float, float>(ApplyKernel);
        this.applyKernelWithRanges = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float, float, float, float, float, float, float, float, ArrayView1D<float, Stride1D.Dense>, int>(ApplyKernelWithRanges);
        this.applyGammaKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float>(ApplyGammaKernel);
    }

    protected override void ApplyInPlace(ArrayView1D<Rgb, Stride1D.Dense> gpuPixels, EffectiveToneMapperSettings effectiveSettings)
    {
        var pixelCount = (int)gpuPixels.Length;

        using var sum = this.accelerator.Allocate1D<float>(1);
        sum.MemSetToZero();
        this.logSumKernel(pixelCount, gpuPixels, sum.View);
        this.accelerator.Synchronize();
        var avgLum = GpuHelper.Exp(sum.GetAsArray1D()[0] / pixelCount);

        var luminanceScale = XMath.Max(0f, this.settings.Luminance) * GpuHelper.Pow(2f, effectiveSettings.ExposureEV);
        var blackClip = XMath.Clamp(this.settings.BlackClip, 0f, 0.99f);
        var whiteClip = XMath.Clamp(this.settings.WhiteClip, blackClip + 1e-3f, 4f);
        var saturationRanges = this.settings.GetSaturationColorRanges();

        if (saturationRanges.Length == 0)
        {
            this.applyKernel(
                pixelCount,
                gpuPixels,
                avgLum,
                GetBalanceStrength(this.settings, effectiveSettings, this.ForceToneMappingCore),
                XMath.Max(this.settings.ToneCompression, 1e-3f),
                XMath.Max(this.settings.LightingEffect, 0f),
                luminanceScale,
                blackClip,
                whiteClip,
                XMath.Max(effectiveSettings.Contrast, 0f),
                XMath.Max(effectiveSettings.Brightness, 0f),
                XMath.Max(effectiveSettings.Saturation, 0f));
        }
        else
        {
            var packedRanges = PackSaturationRanges(saturationRanges);
            using var ranges = this.accelerator.Allocate1D(packedRanges);
            this.applyKernelWithRanges(
                pixelCount,
                gpuPixels,
                this.SourcePixelsBeforeProcessing,
                avgLum,
                GetBalanceStrength(this.settings, effectiveSettings, this.ForceToneMappingCore),
                XMath.Max(this.settings.ToneCompression, 1e-3f),
                XMath.Max(this.settings.LightingEffect, 0f),
                luminanceScale,
                blackClip,
                whiteClip,
                XMath.Max(effectiveSettings.Contrast, 0f),
                XMath.Max(effectiveSettings.Brightness, 0f),
                XMath.Max(effectiveSettings.Saturation, 0f),
                ranges.View,
                saturationRanges.Length);
        }

        var gamma = XMath.Max(effectiveSettings.Gamma, 0.1f);
        if (XMath.Abs(gamma - 1f) > 1e-3f)
        {
            this.applyGammaKernel(pixelCount, gpuPixels, 1f / gamma);
        }

        this.accelerator.Synchronize();
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

    private static void LogSumKernel(Index1D index, ArrayView1D<Rgb, Stride1D.Dense> pixels, ArrayView1D<float, Stride1D.Dense> sum)
    {
        var lum = XMath.Max(pixels[index].Light(), Epsilon);
        Atomic.Add(ref sum[0], GpuHelper.Log(lum));
    }

    private static void ApplyKernel(
        Index1D index,
        ArrayView1D<Rgb, Stride1D.Dense> pixels,
        float avgLum,
        float strength,
        float toneCompression,
        float lightingEffect,
        float luminanceScale,
        float blackClip,
        float whiteClip,
        float contrast,
        float brightness,
        float saturation)
    {
        var rgb = pixels[index];
        var sourceLum = XMath.Max(rgb.Light(), Epsilon);
        var normalizedLum = (sourceLum * luminanceScale) / ((sourceLum * luminanceScale) + toneCompression);
        var adaptedLum = avgLum + ((normalizedLum - avgLum) * lightingEffect);
        adaptedLum = XMath.Clamp((adaptedLum - blackClip) / (whiteClip - blackClip), 0f, 1f);
        adaptedLum = XMath.Clamp(((adaptedLum - 0.5f) * contrast) + 0.5f, 0f, 1f);
        adaptedLum = XMath.Clamp(adaptedLum * brightness, 0f, 1f);
        var mappedLum = sourceLum + ((adaptedLum - sourceLum) * strength);

        var scale = mappedLum / sourceLum;
        rgb.Red *= scale;
        rgb.Green *= scale;
        rgb.Blue *= scale;

        rgb.Red = mappedLum + ((rgb.Red - mappedLum) * saturation);
        rgb.Green = mappedLum + ((rgb.Green - mappedLum) * saturation);
        rgb.Blue = mappedLum + ((rgb.Blue - mappedLum) * saturation);

        rgb.Red = XMath.Clamp(rgb.Red, 0f, 1f);
        rgb.Green = XMath.Clamp(rgb.Green, 0f, 1f);
        rgb.Blue = XMath.Clamp(rgb.Blue, 0f, 1f);
        pixels[index] = rgb;
    }

    private static void ApplyKernelWithRanges(
        Index1D index,
        ArrayView1D<Rgb, Stride1D.Dense> pixels,
        ArrayView1D<Rgb, Stride1D.Dense> sourcePixels,
        float avgLum,
        float strength,
        float toneCompression,
        float lightingEffect,
        float luminanceScale,
        float blackClip,
        float whiteClip,
        float contrast,
        float brightness,
        float saturation,
        ArrayView1D<float, Stride1D.Dense> ranges,
        int rangeCount)
    {
        var rgb = pixels[index];
        var sourceRgb = sourcePixels[index];
        var sourceLum = XMath.Max(rgb.Light(), Epsilon);
        var normalizedLum = (sourceLum * luminanceScale) / ((sourceLum * luminanceScale) + toneCompression);
        var adaptedLum = avgLum + ((normalizedLum - avgLum) * lightingEffect);
        adaptedLum = XMath.Clamp((adaptedLum - blackClip) / (whiteClip - blackClip), 0f, 1f);
        adaptedLum = XMath.Clamp(((adaptedLum - 0.5f) * contrast) + 0.5f, 0f, 1f);
        adaptedLum = XMath.Clamp(adaptedLum * brightness, 0f, 1f);
        var mappedLum = sourceLum + ((adaptedLum - sourceLum) * strength);

        var scale = mappedLum / sourceLum;
        rgb.Red *= scale;
        rgb.Green *= scale;
        rgb.Blue *= scale;

        var adjustedSaturation = ApplySaturationRanges(saturation, sourceRgb, ranges, rangeCount);
        rgb.Red = mappedLum + ((rgb.Red - mappedLum) * adjustedSaturation);
        rgb.Green = mappedLum + ((rgb.Green - mappedLum) * adjustedSaturation);
        rgb.Blue = mappedLum + ((rgb.Blue - mappedLum) * adjustedSaturation);

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
        if (width >= domainWidth - Epsilon)
        {
            return 1f;
        }

        if (width <= Epsilon)
        {
            return XMath.Abs(value - min) <= Epsilon ? 1f : 0f;
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
        if (XMath.Abs(max - min) >= 360f - Epsilon)
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
            return XMath.Min(NormalizeHue(hue - min), NormalizeHue(min - hue)) <= Epsilon ? 1f : 0f;
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
        saturation = max <= Epsilon ? 0f : delta / max;

        if (delta <= Epsilon)
        {
            hue = 0f;
            return;
        }

        if (max == r)
        {
            hue = 60f * GpuHelper.RemFloat((g - b) / delta, 6f);
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
