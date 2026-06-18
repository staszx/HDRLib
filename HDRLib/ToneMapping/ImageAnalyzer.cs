// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using Image;

public sealed class ImageAdjustSettings
{
    #region Properties

    public float ExposureEV { get; init; } // �� V3 (log-based)
    public float Contrast { get; init; } // �� �����������
    public float Brightness { get; init; } // �� �����������
    public float Saturation { get; init; } // �������������
    public float HighlightCompression { get; init; } // �������������
    public float DynamicRangeStops { get; init; } // ��� �������

    #endregion

    #region Methods

    public override string ToString()
    {
        return $"EV={this.ExposureEV:F3}, " + $"Contrast={this.Contrast:F3}, " + $"Brightness={this.Brightness:F3}, " +
               $"Saturation={this.Saturation:F3}, " + $"HighlightCompression={this.HighlightCompression:F3}, " + $"DR={this.DynamicRangeStops:F3}";
    }

    #endregion
}

internal static class ImageAnalyzer
{
    #region Constants

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

    #endregion

    #region Methods

    public static ImageAdjustSettings Analyze(Rgb[] pixels)
    {
        var total = pixels.Length;
        if (total == 0)
        {
            return new ImageAdjustSettings
            {
                ExposureEV = 0,
                Contrast = 1.0f,
                Brightness = 1.0f,
                Saturation = 1.0f,
                HighlightCompression = 1.0f,
                DynamicRangeStops = 0
            };
        }

        var hist = new int[HistBins];
        var luminance = new float[total];
        BuildHistogramAndLuminance(pixels, hist, luminance);

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

    private static float ComputeAutoExposure(float[] luminance, out float drStops)
    {
        
        var n = luminance.Length;
        var logL = new float[n];

        for (var i = 0; i < n; i++)
        {
            var l = luminance[i];
            if (l < 0)
            {
                l = 0;
            }

            logL[i] = MathF.Log(l + ExposureEpsilon, 2.0f);
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

        var lmid = MathF.Pow(2.0f, lnMid);

        var EV = MathF.Log(ExposureTargetMid / (lmid + ExposureEpsilon), 2.0f);

        return Math.Clamp(EV, ExposureEvMin, ExposureEvMax);
    }

    private static void BuildHistogramAndLuminance(Rgb[] px, int[] hist, float[] luminance)
    {
        Array.Clear(hist);
        float scale = HistBins - 1;

        for (var i = 0; i < px.Length; i++)
        {
            var p = px[i];
            var l = p.Light();
            luminance[i] = l;

            var idx = (int)(l * scale);
            idx = Math.Clamp(idx, 0, HistBins - 1);
            hist[idx]++;
        }
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
        var minC = ContrastMin;
        var maxC = ContrastMax;

        var contrast = minC + midMass * (maxC - minC);
        return Math.Clamp(contrast, minC, maxC);
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

    private static float ComputeHighlightCompression(int[] hist, int total)
    {
        var start = (int)(HighlightRangeStart * HistBins);
        var count = 0;

        for (var i = start; i < HistBins; i++)
        {
            count += hist[i];
        }

        var hiMass = (float)count / total;

        if (hiMass > HighlightMassHigh)
        {
            return HighlightCompressionStrong;
        }

        if (hiMass > HighlightMassMedium)
        {
            return HighlightCompressionMedium;
        }

        return HighlightCompressionOff;
    }

    private static float PercentileSorted(float[] sorted, float p)
    {
        var n = sorted.Length;

        var pos = p * (n - 1);
        var idx = (int)pos;
        var frac = pos - idx;

        if (idx >= n - 1)
        {
            return sorted[n - 1];
        }

        return sorted[idx] * (1.0f - frac) + sorted[idx + 1] * frac;
    }

    #endregion
}
