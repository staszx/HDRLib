// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using HDRLib.Adjust;
using HDRLib.Gpu;
using HDRLib.Image;
using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;

internal sealed class ImageAnalyzerGpu
{
    private const int HistBins = 256;
    private const float SaturationBase = 1.0f;
    private const float SaturationContrastFactor = 0.40f;
    private const float SaturationMin = 1.0f;
    private const float SaturationMax = 1.35f;
    private const float ExposureEpsilon = 1e-6f;
    private const float ExposureTargetMid = 0.18f;
    private const float ExposureEvMin = -6.0f;
    private const float ExposureEvMax = 6.0f;
    private const float ContrastRangeStart = 0.35f;
    private const float ContrastRangeEnd = 0.65f;
    private const float ContrastMin = 1.15f;
    private const float ContrastMax = 1.60f;
    private const float BrightnessRangeStart = 0.25f;
    private const float BrightnessRangeEnd = 0.75f;
    private const float BrightnessTarget = 0.50f;
    private const float BrightnessMin = 0.7f;
    private const float BrightnessMax = 1.4f;
    private const float ShadowRangeEnd = 0.25f;
    private const float ShadowPercentile = 0.20f;
    private const float ShadowTarget = 0.28f;
    private const float ShadowMassMin = 0.10f;
    private const float ShadowMassMax = 0.55f;
    private const float ShadowMin = 1.0f;
    private const float ShadowMax = 1.35f;
    private const float MidtoneMin = 0.90f;
    private const float MidtoneMax = 1.20f;
    private const float HighlightRangeStart = 0.85f;
    private const float HighlightPercentile = 0.95f;
    private const float HighlightPercentileStart = 0.85f;
    private const float HighlightMassStart = 0.05f;
    private const float HighlightMassMax = 0.25f;
    private const float HighlightCompressionOff = 1.00f;
    private const float HighlightCompressionMax = 1.35f;

    private readonly Accelerator accelerator;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>> buildHistogramKernel;

    public ImageAnalyzerGpu(GpuContext context)
    {
        this.accelerator = context.Accelerator;
        this.buildHistogramKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>>(BuildHistogramAndLuminanceKernel);
    }

    public ImageAdjustSettings Analyze(ArrayView1D<Rgb, Stride1D.Dense> gpuPixels)
    {
        var total = (int)gpuPixels.Length;
        if (total == 0)
        {
            return new ImageAdjustSettings
            {
                ExposureEV = 0,
                Contrast = 1.0f,
                Brightness = 1.0f,
                Shadows = 1.0f,
                Midtones = 1.0f,
                Saturation = 1.0f,
                HighlightCompression = 1.0f,
                DynamicRangeStops = 0
            };
        }

        using var histGpu = this.accelerator.Allocate1D<int>(HistBins);
        using var luminanceGpu = this.accelerator.Allocate1D<float>(total);
        histGpu.MemSetToZero();

        this.buildHistogramKernel(total, gpuPixels, histGpu.View, luminanceGpu.View);
        this.accelerator.Synchronize();

        var hist = histGpu.GetAsArray1D();
        var luminance = luminanceGpu.GetAsArray1D();

        var exposureEV = ComputeAutoExposure(luminance, out var drStops);
        var contrast = ComputeContrastFromHistogram(hist, total);
        var brightness = ComputeBrightnessFromHistogram(hist);
        var shadows = ComputeShadowsFromHistogram(hist, total);
        var midtones = ComputeMidtonesFromHistogram(hist);
        var saturation = SaturationBase + ((contrast - SaturationBase) * SaturationContrastFactor);
        saturation = Math.Clamp(saturation, SaturationMin, SaturationMax);

        var highlights = ComputeHighlightCompression(hist, total);
        return new ImageAdjustSettings
        {
            ExposureEV = exposureEV,
            Contrast = contrast,
            Brightness = brightness,
            Shadows = shadows,
            Midtones = midtones,
            Saturation = saturation,
            HighlightCompression = highlights,
            DynamicRangeStops = drStops
        };
    }

    private static void BuildHistogramAndLuminanceKernel(Index1D index, ArrayView1D<Rgb, Stride1D.Dense> px, ArrayView1D<int, Stride1D.Dense> hist, ArrayView1D<float, Stride1D.Dense> luminance)
    {
        var l = px[index].Light();
        luminance[index] = l;

        var idx = (int)(l * (HistBins - 1));
        idx = XMath.Clamp(idx, 0, HistBins - 1);
        Atomic.Add(ref hist[idx], 1);
    }

    private static float ComputeAutoExposure(float[] luminance, out float drStops)
    {
        var n = luminance.Length;
        var logL = new float[n];
        for (var i = 0; i < n; i++)
        {
            var l = Math.Max(luminance[i], 0);
            logL[i] = GpuHelper.Log(l + ExposureEpsilon) / 0.69314718056f;
        }

        Array.Sort(logL);
        var ln5 = PercentileSorted(logL, 0.05f);
        var ln95 = PercentileSorted(logL, 0.95f);
        drStops = ln95 - ln5;

        for (var i = 0; i < n; i++)
        {
            logL[i] = Math.Clamp(logL[i], ln5, ln95);
        }

        var lnMid = PercentileSorted(logL, 0.50f);
        var lmid = GpuHelper.Pow(2.0f, lnMid);
        var ev = GpuHelper.Log(ExposureTargetMid / (lmid + ExposureEpsilon)) / 0.69314718056f;

        return Math.Clamp(ev, ExposureEvMin, ExposureEvMax);
    }

    private static float ComputeContrastFromHistogram(int[] hist, int total)
    {
        var start = (int)(ContrastRangeStart * HistBins);
        var end = (int)(ContrastRangeEnd * HistBins);
        var midCount = 0;
        for (var i = start; i <= end; i++)
        {
            midCount += hist[i];
        }

        var midMass = (float)midCount / total;
        return Math.Clamp(ContrastMin + (midMass * (ContrastMax - ContrastMin)), ContrastMin, ContrastMax);
    }

    private static float ComputeBrightnessFromHistogram(int[] hist)
    {
        float sum = 0;
        var count = 0;
        var start = (int)(BrightnessRangeStart * HistBins);
        var end = (int)(BrightnessRangeEnd * HistBins);

        for (var i = start; i <= end; i++)
        {
            var l = (float)i / (HistBins - 1);
            var freq = hist[i];
            sum += l * freq;
            count += freq;
        }

        if (count == 0)
        {
            return 1.0f;
        }

        return Math.Clamp(BrightnessTarget / (sum / count), BrightnessMin, BrightnessMax);
    }

    private static float ComputeShadowsFromHistogram(int[] hist, int total)
    {
        var shadowEnd = (int)(ShadowRangeEnd * (HistBins - 1));
        var shadowCount = 0;
        for (var i = 0; i <= shadowEnd; i++)
        {
            shadowCount += hist[i];
        }

        var shadowMass = (float)shadowCount / total;
        var p20 = PercentileFromHistogram(hist, total, ShadowPercentile);
        var darkness = Math.Clamp((ShadowTarget - p20) / ShadowTarget, 0f, 1f);
        var mass = Math.Clamp((shadowMass - ShadowMassMin) / (ShadowMassMax - ShadowMassMin), 0f, 1f);
        return Math.Clamp(1f + (darkness * mass * (ShadowMax - 1f)), ShadowMin, ShadowMax);
    }

    private static float ComputeMidtonesFromHistogram(int[] hist)
    {
        float sum = 0;
        var count = 0;
        var start = (int)(ContrastRangeStart * HistBins);
        var end = (int)(ContrastRangeEnd * HistBins);

        for (var i = start; i <= end; i++)
        {
            var l = (float)i / (HistBins - 1);
            var freq = hist[i];
            sum += l * freq;
            count += freq;
        }

        if (count == 0)
        {
            return 1.0f;
        }

        var balance = Math.Clamp((BrightnessTarget - (sum / count)) / BrightnessTarget, -1f, 1f);
        return Math.Clamp(1f + (balance * 0.20f), MidtoneMin, MidtoneMax);
    }

    private static float ComputeHighlightCompression(int[] hist, int total)
    {
        var start = (int)(HighlightRangeStart * HistBins);
        var count = 0;
        for (var i = start; i < HistBins; i++)
        {
            count += hist[i];
        }

        var hiMass = (float)count / total;
        var massPressure = Math.Clamp((hiMass - HighlightMassStart) / (HighlightMassMax - HighlightMassStart), 0f, 1f);
        var p95 = PercentileFromHistogram(hist, total, HighlightPercentile);
        var percentilePressure = Math.Clamp((p95 - HighlightPercentileStart) / (1f - HighlightPercentileStart), 0f, 1f);
        var pressure = Math.Max(massPressure, percentilePressure);
        return Math.Clamp(HighlightCompressionOff + (pressure * (HighlightCompressionMax - HighlightCompressionOff)), HighlightCompressionOff, HighlightCompressionMax);
    }

    private static float PercentileSorted(float[] sorted, float p)
    {
        var n = sorted.Length;
        var pos = p * (n - 1);
        var idx = (int)pos;
        var frac = pos - idx;
        if (idx >= n - 1) return sorted[n - 1];
        return (sorted[idx] * (1.0f - frac)) + (sorted[idx + 1] * frac);
    }

    private static float PercentileFromHistogram(int[] hist, int total, float p)
    {
        var target = p * Math.Max(total - 1, 0);
        var cumulative = 0;

        for (var i = 0; i < hist.Length; i++)
        {
            cumulative += hist[i];
            if (cumulative > target)
            {
                return (float)i / (HistBins - 1);
            }
        }

        return 1.0f;
    }
}
