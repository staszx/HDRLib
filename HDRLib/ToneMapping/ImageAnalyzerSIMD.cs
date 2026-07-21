// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using HDRLib.MathUtils;

internal static class ImageAnalyzerSIMD
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
    private const float InvLn2 = 1.4426950408889634f;

    public static ImageAdjustSettings Analyze(Vector256<float>[][] pixels)
    {
        var total = pixels[0].Length * Vector256<float>.Count;
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

        var hist = new int[HistBins];
        var luminance = GC.AllocateUninitializedArray<Vector256<float>>(pixels[0].Length);
        BuildHistogramAndLuminance(pixels, hist, luminance);

        var exposureEV = ComputeAutoExposure(luminance, out var drStops);
        var contrast = ComputeContrastFromHistogram(hist, total);
        var brightness = ComputeBrightnessFromHistogram(hist);
        var shadows = ComputeShadowsFromHistogram(hist, total);
        var midtones = ComputeMidtonesFromHistogram(hist);
        var saturation = SaturationBase + ((contrast - SaturationBase) * SaturationContrastFactor);
        saturation = Math.Clamp(saturation, SaturationMin, SaturationMax);

        var hc = ComputeHighlightCompression(hist, total);
        return new ImageAdjustSettings
        {
            ExposureEV = exposureEV,
            Contrast = contrast,
            Brightness = brightness,
            Shadows = shadows,
            Midtones = midtones,
            Saturation = saturation,
            HighlightCompression = hc,
            DynamicRangeStops = drStops
        };
    }

    private static void BuildHistogramAndLuminance(Vector256<float>[][] px, int[] hist, Vector256<float>[] luminance)
    {
        Array.Clear(hist);

        var redWeight = Vector256.Create(0.2126f);
        var greenWeight = Vector256.Create(0.7152f);
        var blueWeight = Vector256.Create(0.0722f);
        var scale = HistBins - 1;

        var red = px[0];
        var green = px[1];
        var blue = px[2];

        for (var i = 0; i < red.Length; i++)
        {
            var lVector = (red[i] * redWeight) + (green[i] * greenWeight) + (blue[i] * blueWeight);
            luminance[i] = lVector;

            for (var lane = 0; lane < Vector256<float>.Count; lane++)
            {
                var hIdx = (int)(lVector[lane] * scale);
                hIdx = Math.Clamp(hIdx, 0, HistBins - 1);
                hist[hIdx]++;
            }
        }
    }

    private static float ComputeAutoExposure(Vector256<float>[] luminance, out float drStops)
    {
        var n = luminance.Length * Vector256<float>.Count;
        var safeLum = GC.AllocateUninitializedArray<Vector256<float>>(luminance.Length);
        var logL = GC.AllocateUninitializedArray<Vector256<float>>(luminance.Length);
        var sortedLogL = GC.AllocateUninitializedArray<float>(n);

        var zero = Vector256<float>.Zero;
        var epsilon = Vector256.Create(ExposureEpsilon);
        var invLn2 = Vector256.Create(InvLn2);

        for (var i = 0; i < luminance.Length; i++)
        {
            safeLum[i] = Vector256.Max(luminance[i], zero) + epsilon;
            logL[i] = AvxMath.Ln(safeLum[i]) * invLn2;
            logL[i].CopyTo(sortedLogL.AsSpan(i * Vector256<float>.Count, Vector256<float>.Count));
        }

        Array.Sort(sortedLogL);

        var percentile5 = PercentileSorted(sortedLogL, 0.05f);
        var percentile95 = PercentileSorted(sortedLogL, 0.95f);
        var ln5 = MathF.Min(percentile5, percentile95);
        var ln95 = MathF.Max(percentile5, percentile95);
        drStops = ln95 - ln5;

        var minV = Vector256.Create(ln5);
        var maxV = Vector256.Create(ln95);
        var vectorCount = n / Vector256<float>.Count;
        var tailStart = vectorCount * Vector256<float>.Count;

        for (var i = 0; i < vectorCount; i++)
        {
            var idx = i * Vector256<float>.Count;
            var value = Vector256.LoadUnsafe(ref sortedLogL[idx]);
            value = Avx.Min(maxV, Avx.Max(minV, value));
            value.StoreUnsafe(ref sortedLogL[idx]);
        }

        for (var i = tailStart; i < n; i++)
        {
            sortedLogL[i] = Math.Clamp(sortedLogL[i], ln5, ln95);
        }

        var lnMid = PercentileSorted(sortedLogL, 0.50f);
        var lmid = MathF.Pow(2.0f, lnMid);
        var ev = MathF.Log2(ExposureTargetMid / (lmid + ExposureEpsilon));

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
        var contrast = ContrastMin + (midMass * (ContrastMax - ContrastMin));
        return Math.Clamp(contrast, ContrastMin, ContrastMax);
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

        var avgMid = sum / count;
        var brightness = BrightnessTarget / avgMid;
        return Math.Clamp(brightness, BrightnessMin, BrightnessMax);
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
        var i = (int)pos;
        var frac = pos - i;

        if (i >= n - 1)
        {
            return sorted[n - 1];
        }

        return (sorted[i] * (1.0f - frac)) + (sorted[i + 1] * frac);
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
