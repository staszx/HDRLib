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
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float> applyGammaKernel;

    public ContrastBalancerToneMapperGpu(GpuContext context, ContrastBalancerToneMapperSettings settings) : base(context, settings)
    {
        this.accelerator = context.Accelerator;
        this.settings = settings;
        this.logSumKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>>(LogSumKernel);
        this.applyKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float, float, float, float, float, float, float, float>(ApplyKernel);
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

        this.applyKernel(
            pixelCount,
            gpuPixels,
            avgLum,
            GetEffectiveStrength(this.settings),
            XMath.Max(this.settings.ToneCompression, 1e-3f),
            XMath.Max(this.settings.LightingEffect, 0f),
            luminanceScale,
            blackClip,
            whiteClip,
            XMath.Max(effectiveSettings.Contrast, 0f),
            XMath.Max(effectiveSettings.Brightness, 0f),
            XMath.Max(effectiveSettings.Saturation, 0f));

        var gamma = XMath.Max(effectiveSettings.Gamma, 0.1f);
        if (XMath.Abs(gamma - 1f) > 1e-3f)
        {
            this.applyGammaKernel(pixelCount, gpuPixels, 1f / gamma);
        }

        this.accelerator.Synchronize();
    }

    private static float GetEffectiveStrength(ContrastBalancerToneMapperSettings settings)
    {
        var strength = Math.Clamp(settings.Strength, 0f, 1f);
        if (strength > Epsilon)
        {
            return strength;
        }

        return HasActiveToneControls(settings) ? 1f : 0f;
    }

    private static bool HasActiveToneControls(ContrastBalancerToneMapperSettings settings)
    {
        return MathF.Abs(settings.ToneCompression - 1f) > Epsilon ||
               MathF.Abs(settings.LightingEffect - 1f) > Epsilon ||
               MathF.Abs(settings.Luminance - 1f) > Epsilon ||
               MathF.Abs(settings.WhiteClip - ClippedToneMapperSettings.NeutralWhiteClip) > Epsilon ||
               MathF.Abs(settings.BlackClip - ClippedToneMapperSettings.NeutralBlackClip) > Epsilon;
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

    private static void ApplyGammaKernel(Index1D index, ArrayView1D<Rgb, Stride1D.Dense> pixels, float invGamma)
    {
        var rgb = pixels[index];
        rgb.Red = XMath.Clamp(GpuHelper.Pow(rgb.Red, invGamma), 0f, 1f);
        rgb.Green = XMath.Clamp(GpuHelper.Pow(rgb.Green, invGamma), 0f, 1f);
        rgb.Blue = XMath.Clamp(GpuHelper.Pow(rgb.Blue, invGamma), 0f, 1f);
        pixels[index] = rgb;
    }
}
