// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using Adjust;
using HDRLib.Gpu;
using HDRLib.Image;
using HDRLib.ToneMapping.Settings;
using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;

internal sealed class AcesFilmicToneMapperGpu : ToneMapperGpu
{
    private readonly Accelerator accelerator;
    private readonly AcesFilmicTonemapperSettings settings;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, float> exposureLogSumKernel;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float, float> toneMapKernel;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float> gammaKernel;

    public AcesFilmicToneMapperGpu(GpuContext context, AcesFilmicTonemapperSettings settings) : base(context, settings)
    {
        this.accelerator = context.Accelerator;
        this.settings = settings;
        this.exposureLogSumKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, float>(ToneMapperUtilities.ExposureLogSumKernel);
        this.toneMapKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float, float>(ToneMapKernel);
        this.gammaKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float>(ApplyGammaKernel);
    }

    protected override void ApplyInPlace(ArrayView1D<Rgb, Stride1D.Dense> gpuPixels, EffectiveToneMapperSettings effectiveSettings)
    {
        var exposureAuto = this.Exposure(gpuPixels);
        var exposureManual = GpuHelper.Pow(2.0f, effectiveSettings.ExposureEV);
        var exposure = exposureAuto * exposureManual;

        this.toneMapKernel((int)gpuPixels.Length, gpuPixels, exposure, effectiveSettings.Brightness, effectiveSettings.Contrast, effectiveSettings.Saturation);

        var gamma = XMath.Max(effectiveSettings.Gamma, 0.1f);
        if (XMath.Abs(gamma - 1f) > 1e-3f)
        {
            this.gammaKernel((int)gpuPixels.Length, gpuPixels, 1f / gamma);
        }
    }

    private float Exposure(ArrayView1D<Rgb, Stride1D.Dense> gpuPixels)
    {
        return ToneMapperUtilities.ComputeAutoExposure(this.accelerator, gpuPixels, this.exposureLogSumKernel, this.settings.Key, AcesConstants.ExposureDelta, AcesConstants.ExposureEpsilon);
    }

    private static void ToneMapKernel(Index1D index, ArrayView1D<Rgb, Stride1D.Dense> pixels, float exposure, float brightness, float contrast, float saturation)
    {
        var p = pixels[index];

        var r = p.Red * exposure;
        var g = p.Green * exposure;
        var b = p.Blue * exposure;

        var acesR = (r * AcesConstants.Input00) + (g * AcesConstants.Input01) + (b * AcesConstants.Input02);
        var acesG = (r * AcesConstants.Input10) + (g * AcesConstants.Input11) + (b * AcesConstants.Input12);
        var acesB = (r * AcesConstants.Input20) + (g * AcesConstants.Input21) + (b * AcesConstants.Input22);

        acesR = AcesFitted(acesR);
        acesG = AcesFitted(acesG);
        acesB = AcesFitted(acesB);

        r = ((acesR * AcesConstants.Output00) + (acesG * AcesConstants.Output01) + (acesB * AcesConstants.Output02)) * brightness;
        g = ((acesR * AcesConstants.Output10) + (acesG * AcesConstants.Output11) + (acesB * AcesConstants.Output12)) * brightness;
        b = ((acesR * AcesConstants.Output20) + (acesG * AcesConstants.Output21) + (acesB * AcesConstants.Output22)) * brightness;

        r = AdjustContrast(r, contrast);
        g = AdjustContrast(g, contrast);
        b = AdjustContrast(b, contrast);

        if (XMath.Abs(saturation - 1f) > 1e-6f)
        {
            var lum = (r * 0.2126f) + (g * 0.7152f) + (b * 0.0722f);
            r = lum + ((r - lum) * saturation);
            g = lum + ((g - lum) * saturation);
            b = lum + ((b - lum) * saturation);
        }

        pixels[index] = new Rgb(
            XMath.Clamp(r, AcesConstants.ChannelMin, AcesConstants.ChannelMax),
            XMath.Clamp(g, AcesConstants.ChannelMin, AcesConstants.ChannelMax),
            XMath.Clamp(b, AcesConstants.ChannelMin, AcesConstants.ChannelMax));
    }

    private static void ApplyGammaKernel(Index1D index, ArrayView1D<Rgb, Stride1D.Dense> pixels, float invGamma)
    {
        var p = pixels[index];
        pixels[index] = new Rgb(
            XMath.Clamp(GpuHelper.Pow(p.Red, invGamma), AcesConstants.ChannelMin, AcesConstants.ChannelMax),
            XMath.Clamp(GpuHelper.Pow(p.Green, invGamma), AcesConstants.ChannelMin, AcesConstants.ChannelMax),
            XMath.Clamp(GpuHelper.Pow(p.Blue, invGamma), AcesConstants.ChannelMin, AcesConstants.ChannelMax));
    }

    private static float AcesFitted(float x)
    {
        x = XMath.Max(x, 0f);
        var num = (x * (x + AcesConstants.FitA)) - AcesConstants.FitB;
        var den = (x * ((AcesConstants.FitC * x) + AcesConstants.FitD)) + AcesConstants.FitE;
        return XMath.Max(num / den, AcesConstants.ChannelMin);
    }

    private static float AdjustContrast(float value, float contrast)
    {
        return XMath.Clamp(((value - AcesConstants.ContrastPivot) * contrast) + AcesConstants.ContrastPivot, AcesConstants.ChannelMin, AcesConstants.ChannelMax);
    }
}
