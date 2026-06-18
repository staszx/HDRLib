// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using Adjust;
using HDRLib.Gpu;
using HDRLib.Image;
using HDRLib.ToneMapping.Settings;
using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;

internal sealed class AutoAdjustToneMapperGpu : ToneMapperGpu
{
    private const int AdjustBrightnessFlag = 1;
    private const int AdjustContrastFlag = 2;
    private const int AdjustSaturationFlag = 4;

    private readonly Accelerator accelerator;
    private readonly AutoAdjustTonemapperSettings settings;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>> extractLuminanceKernel;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float, float, float, float, float, float, float, int> applyKernel;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float> applyGammaKernel;

    public AutoAdjustToneMapperGpu(GpuContext context, AutoAdjustTonemapperSettings settings) : base(context, settings)
    {
        this.accelerator = context.Accelerator;
        this.settings = settings;
        this.extractLuminanceKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>>(ExtractLuminanceKernel);
        this.applyKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float, float, float, float, float, float, float, int>(ApplyKernel);
        this.applyGammaKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float>(ApplyGammaKernel);
    }

    protected override void ApplyInPlace(ArrayView1D<Rgb, Stride1D.Dense> gpuPixels, EffectiveToneMapperSettings effectiveSettings)
    {
        var pixelCount = (int)gpuPixels.Length;
        using var luminanceBuffer = this.accelerator.Allocate1D<float>(pixelCount);
        this.extractLuminanceKernel(pixelCount, gpuPixels, luminanceBuffer.View);
        this.accelerator.Synchronize();

        var luminance = luminanceBuffer.GetAsArray1D();
        var sumLum = 0f;
        for (var i = 0; i < luminance.Length; i++)
        {
            sumLum += Math.Clamp(luminance[i], 0f, 1f);
        }

        var avgLum255 = (sumLum / pixelCount) * 255f;
        var stdDev255 = ComputeStdDev255(luminance, avgLum255 / 255f);

        var exposureScale = GpuHelper.Pow(2f, effectiveSettings.ExposureEV);
        var brightnessScale = exposureScale * effectiveSettings.Brightness;
        if (this.settings.AdjustBrightness)
        {
            var brightnessFactor = ComputeCorrectionFactor(avgLum255, this.settings.TargetLuminance255);
            brightnessScale *= 1f + (XMath.Max(brightnessFactor, 0f) * this.settings.MaskStrengthScale);
        }

        var contrastScale = effectiveSettings.Contrast;
        if (this.settings.AdjustContrast)
        {
            var contrastFactor = ComputeCorrectionFactor(stdDev255, this.settings.TargetContrastStdDev255);
            contrastScale *= (1f + contrastFactor) * 0.9f;
        }

        var flags = 0;
        if (this.settings.AdjustBrightness)
        {
            flags |= AdjustBrightnessFlag;
        }

        if (this.settings.AdjustContrast)
        {
            flags |= AdjustContrastFlag;
        }

        if (this.settings.AdjustSaturation)
        {
            flags |= AdjustSaturationFlag;
        }

        this.applyKernel(
            pixelCount,
            gpuPixels,
            XMath.Max(1f, brightnessScale),
            contrastScale,
            this.settings.ShadowsBoost,
            this.settings.MidtonesBoost,
            this.settings.HighlightsBoost,
            this.settings.SaturationMin,
            this.settings.SaturationMid,
            this.settings.SaturationStrength,
            effectiveSettings.Saturation,
            flags);

        var gamma = XMath.Max(effectiveSettings.Gamma, 0.1f);
        if (XMath.Abs(gamma - 1f) > 1e-3f)
        {
            this.applyGammaKernel(pixelCount, gpuPixels, 1f / gamma);
        }

        this.accelerator.Synchronize();
    }

    private static void ExtractLuminanceKernel(Index1D index, ArrayView1D<Rgb, Stride1D.Dense> pixels, ArrayView1D<float, Stride1D.Dense> luminance)
    {
        luminance[index] = XMath.Clamp(pixels[index].Light(), 0f, 1f);
    }

    private static void ApplyKernel(
        Index1D index,
        ArrayView1D<Rgb, Stride1D.Dense> pixels,
        float brightnessScale,
        float contrastScale,
        float shadowsBoost,
        float midtonesBoost,
        float highlightsBoost,
        float saturationMin,
        float saturationMid,
        float saturationStrength,
        float globalSaturation,
        int flags)
    {
        var rgb = pixels[index];
        var lum = XMath.Clamp(rgb.Light(), 0f, 1f);

        if ((flags & AdjustBrightnessFlag) != 0)
        {
            var shadowsWeight = XMath.Clamp((0.5f - lum) / 0.5f, 0f, 1f);
            var highlightsWeight = XMath.Clamp((lum - 0.5f) / 0.5f, 0f, 1f);
            var midtonesWeight = XMath.Clamp(1f - XMath.Abs((lum - 0.5f) * 2f), 0f, 1f);

            var toneBoost =
                (shadowsBoost * shadowsWeight) +
                (midtonesBoost * midtonesWeight) +
                (highlightsBoost * highlightsWeight);

            var boost = XMath.Max(0.01f, 1f + ((toneBoost - 1f) * brightnessScale));
            rgb.Red *= boost;
            rgb.Green *= boost;
            rgb.Blue *= boost;
        }

        if ((flags & AdjustContrastFlag) != 0)
        {
            rgb.Red = ((rgb.Red - 0.5f) * contrastScale) + 0.5f;
            rgb.Green = ((rgb.Green - 0.5f) * contrastScale) + 0.5f;
            rgb.Blue = ((rgb.Blue - 0.5f) * contrastScale) + 0.5f;
        }

        if ((flags & AdjustSaturationFlag) != 0)
        {
            var mappedLum = XMath.Clamp(rgb.Light(), 0f, 1f);
            var midWeight = XMath.Clamp(1f - XMath.Abs((lum - 0.5f) * 2f), 0f, 1f);
            var sat = (saturationMin + ((saturationMid - saturationMin) * midWeight)) * saturationStrength * globalSaturation;
            sat = XMath.Max(0f, sat);

            rgb.Red = mappedLum + ((rgb.Red - mappedLum) * sat);
            rgb.Green = mappedLum + ((rgb.Green - mappedLum) * sat);
            rgb.Blue = mappedLum + ((rgb.Blue - mappedLum) * sat);
        }

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

    private static float ComputeStdDev255(float[] luminance, float mean)
    {
        var varianceSum = 0f;
        for (var i = 0; i < luminance.Length; i++)
        {
            var d = luminance[i] - mean;
            varianceSum += d * d;
        }

        return MathF.Sqrt(varianceSum / luminance.Length) * 255f;
    }

    private static float ComputeCorrectionFactor(float value255, float target255)
    {
        var factor = (target255 - value255) / 255f;
        return Math.Clamp(factor, -0.3f, 0.3f);
    }
}
