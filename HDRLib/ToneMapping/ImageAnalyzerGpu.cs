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
    private const float HighlightRangeStart = 0.85f;
    private const float HighlightMassHigh = 0.20f;
    private const float HighlightMassMedium = 0.10f;
    private const float HighlightCompressionStrong = 0.80f;
    private const float HighlightCompressionMedium = 0.90f;
    private const float HighlightCompressionOff = 1.00f;

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
            return new ImageAdjustSettings { ExposureEV = 0, Contrast = 1.0f, Brightness = 1.0f, Saturation = 1.0f, HighlightCompression = 1.0f, DynamicRangeStops = 0 };
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
        var saturation = SaturationBase + (contrast - SaturationBase) * SaturationContrastFactor;
        saturation = Math.Clamp(saturation, SaturationMin, SaturationMax);

        var hc = ComputeHighlightCompression(hist, total);
        return new ImageAdjustSettings
        {
            ExposureEV = exposureEV,
            Contrast = contrast,
            Brightness = brightness,
            Saturation = saturation,
            HighlightCompression = hc,
            DynamicRangeStops = drStops
        };
    }

    private static void BuildHistogramAndLuminanceKernel(Index1D index, ArrayView1D<Rgb, Stride1D.Dense> px, ArrayView1D<int, Stride1D.Dense> hist, ArrayView1D<float, Stride1D.Dense> luminance)
    {
        const float rw = 0.2126f;
        const float gw = 0.7152f;
        const float bw = 0.0722f;
        var p = px[index];
        var l = rw * p.Red + gw * p.Green + bw * p.Blue;
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
        return Math.Clamp(ContrastMin + midMass * (ContrastMax - ContrastMin), ContrastMin, ContrastMax);
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

    private static float ComputeHighlightCompression(int[] hist, int total)
    {
        var start = (int)(HighlightRangeStart * HistBins);
        var count = 0;
        for (var i = start; i < HistBins; i++)
        {
            count += hist[i];
        }

        var hiMass = (float)count / total;
        if (hiMass > HighlightMassHigh) return HighlightCompressionStrong;
        if (hiMass > HighlightMassMedium) return HighlightCompressionMedium;
        return HighlightCompressionOff;
    }

    private static float PercentileSorted(float[] sorted, float p)
    {
        var n = sorted.Length;
        var pos = p * (n - 1);
        var idx = (int)pos;
        var frac = pos - idx;
        if (idx >= n - 1) return sorted[n - 1];
        return sorted[idx] * (1.0f - frac) + sorted[idx + 1] * frac;
    }
}
