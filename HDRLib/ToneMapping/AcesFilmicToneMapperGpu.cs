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
    private const float DefaultKey = 0.18f;

    private readonly Accelerator accelerator;
    private readonly AcesFilmicTonemapperSettings settings;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, float> exposureLogSumKernel;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float, float, float> toneMapKernel;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float> gammaKernel;

    public AcesFilmicToneMapperGpu(GpuContext context, AcesFilmicTonemapperSettings settings) : base(context, settings)
    {
        this.accelerator = context.Accelerator;
        this.settings = settings;
        this.exposureLogSumKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, float>(ToneMapperUtilities.ExposureLogSumKernel);
        this.toneMapKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float, float, float>(ToneMapKernel);
        this.gammaKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float>(ApplyGammaKernel);
    }

    protected override bool NormalizesInputRange => false;

    protected override void ApplyInPlace(ArrayView1D<Rgb, Stride1D.Dense> gpuPixels, EffectiveToneMapperSettings effectiveSettings)
    {
        var neutralExposureAuto = this.NeutralExposure(gpuPixels);
        var exposureManual = GpuHelper.Pow(2.0f, effectiveSettings.ExposureEV);
        var neutralExposure = neutralExposureAuto * exposureManual;
        var exposure = neutralExposure * (this.settings.Key / DefaultKey);

        this.toneMapKernel((int)gpuPixels.Length, gpuPixels, exposure, neutralExposure, effectiveSettings.Brightness, effectiveSettings.Contrast, effectiveSettings.Saturation);

        var gamma = XMath.Max(effectiveSettings.Gamma, 0.1f);
        if (XMath.Abs(gamma - 1f) > 1e-3f)
        {
            this.gammaKernel((int)gpuPixels.Length, gpuPixels, 1f / gamma);
        }
    }

    private float NeutralExposure(ArrayView1D<Rgb, Stride1D.Dense> gpuPixels)
    {
        return ToneMapperUtilities.ComputeAutoExposure(this.accelerator, gpuPixels, this.exposureLogSumKernel, DefaultKey, AcesConstants.ExposureDelta, AcesConstants.ExposureEpsilon);
    }

    private static void ToneMapKernel(Index1D index, ArrayView1D<Rgb, Stride1D.Dense> pixels, float exposure, float neutralExposure, float brightness, float contrast, float saturation)
    {
        var p = pixels[index];

        var sourceR = p.Red;
        var sourceG = p.Green;
        var sourceB = p.Blue;
        var r = sourceR * exposure;
        var g = sourceG * exposure;
        var b = sourceB * exposure;
        var neutralR = sourceR * neutralExposure;
        var neutralG = sourceG * neutralExposure;
        var neutralB = sourceB * neutralExposure;

        var acesR = MapAcesChannel(r, g, b, AcesConstants.Input00, AcesConstants.Input01, AcesConstants.Input02);
        var acesG = MapAcesChannel(r, g, b, AcesConstants.Input10, AcesConstants.Input11, AcesConstants.Input12);
        var acesB = MapAcesChannel(r, g, b, AcesConstants.Input20, AcesConstants.Input21, AcesConstants.Input22);
        var neutralAcesR = MapAcesChannel(neutralR, neutralG, neutralB, AcesConstants.Input00, AcesConstants.Input01, AcesConstants.Input02);
        var neutralAcesG = MapAcesChannel(neutralR, neutralG, neutralB, AcesConstants.Input10, AcesConstants.Input11, AcesConstants.Input12);
        var neutralAcesB = MapAcesChannel(neutralR, neutralG, neutralB, AcesConstants.Input20, AcesConstants.Input21, AcesConstants.Input22);

        var mappedR = MapOutputChannel(acesR, acesG, acesB, AcesConstants.Output00, AcesConstants.Output01, AcesConstants.Output02);
        var mappedG = MapOutputChannel(acesR, acesG, acesB, AcesConstants.Output10, AcesConstants.Output11, AcesConstants.Output12);
        var mappedB = MapOutputChannel(acesR, acesG, acesB, AcesConstants.Output20, AcesConstants.Output21, AcesConstants.Output22);
        var neutralMappedR = MapOutputChannel(neutralAcesR, neutralAcesG, neutralAcesB, AcesConstants.Output00, AcesConstants.Output01, AcesConstants.Output02);
        var neutralMappedG = MapOutputChannel(neutralAcesR, neutralAcesG, neutralAcesB, AcesConstants.Output10, AcesConstants.Output11, AcesConstants.Output12);
        var neutralMappedB = MapOutputChannel(neutralAcesR, neutralAcesG, neutralAcesB, AcesConstants.Output20, AcesConstants.Output21, AcesConstants.Output22);

        r = sourceR + (mappedR - neutralMappedR);
        g = sourceG + (mappedG - neutralMappedG);
        b = sourceB + (mappedB - neutralMappedB);

        r *= brightness;
        g *= brightness;
        b *= brightness;

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

    private static float MapAcesChannel(float r, float g, float b, float inputR, float inputG, float inputB)
    {
        return AcesFitted((r * inputR) + (g * inputG) + (b * inputB));
    }

    private static float MapOutputChannel(float acesR, float acesG, float acesB, float outputR, float outputG, float outputB)
    {
        return (acesR * outputR) + (acesG * outputG) + (acesB * outputB);
    }
}
