// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Settings;

internal sealed class AutoAdjustToneMapperSIMD : ToneMapperSIMD
{
    private readonly AutoAdjustTonemapperSettings settings;

    public AutoAdjustToneMapperSIMD(AutoAdjustTonemapperSettings settings) : base(settings)
    {
        this.settings = settings;
    }

    protected override void ApplyCoreInPlace(Vector256<float>[][] pixels, int width, int height)
    {
        var count = width * height;
        if (count == 0)
        {
            return;
        }

        var luminance = ToneMapperSIMDHelper.BuildLuminance(pixels[0], pixels[1], pixels[2], count);
        var sumLum = 0f;
        for (var i = 0; i < luminance.Length; i++)
        {
            sumLum += Math.Clamp(luminance[i], 0f, 1f);
        }

        var avgLum255 = (sumLum / count) * 255f;
        var stdDev255 = ComputeStdDev255(luminance, count, avgLum255 / 255f);

        var brightnessScale = MathF.Pow(2f, this.settings.ExposureEV) * this.settings.Brightness;
        if (this.settings.AdjustBrightness)
        {
            var brightnessFactor = ComputeCorrectionFactor(avgLum255, this.settings.TargetLuminance255);
            brightnessScale *= 1f + (MathF.Max(brightnessFactor, 0f) * this.settings.MaskStrengthScale);
        }

        var contrastScale = this.settings.Contrast;
        if (this.settings.AdjustContrast)
        {
            var contrastFactor = ComputeCorrectionFactor(stdDev255, this.settings.TargetContrastStdDev255);
            contrastScale *= (1f + contrastFactor) * 0.9f;
        }

        var half = Vector256.Create(0.5f);
        var one = Vector256.Create(1f);
        var zero = Vector256<float>.Zero;
        var contrastV = Vector256.Create(contrastScale);
        var brightnessScaleV = Vector256.Create(MathF.Max(0.01f, brightnessScale));

        var shadowsBoostV = Vector256.Create(this.settings.ShadowsBoost);
        var midtonesBoostV = Vector256.Create(this.settings.MidtonesBoost);
        var highlightsBoostV = Vector256.Create(this.settings.HighlightsBoost);

        var globalSaturation = SaturationToMultiplier(this.settings.Saturation);
        var satMinV = Vector256.Create(this.settings.SaturationMin * this.settings.SaturationStrength * globalSaturation);
        var satRangeV = Vector256.Create((this.settings.SaturationMid - this.settings.SaturationMin) * this.settings.SaturationStrength * globalSaturation);

        Parallel.For(0, pixels[0].Length, i =>
        {
            var r = pixels[0][i];
            var g = pixels[1][i];
            var b = pixels[2][i];

            var lum = Avx.Add(Avx.Add(Avx.Multiply(r, ToneMapperSIMDHelper.Rw), Avx.Multiply(g, ToneMapperSIMDHelper.Gw)), Avx.Multiply(b, ToneMapperSIMDHelper.Bw));

            if (this.settings.AdjustBrightness)
            {
                var shadowsWeight = ToneMapperSIMDHelper.Clamp01(Avx.Divide(Avx.Subtract(half, lum), half));
                var highlightsWeight = ToneMapperSIMDHelper.Clamp01(Avx.Divide(Avx.Subtract(lum, half), half));
                var midtonesWeight = ToneMapperSIMDHelper.Clamp01(Avx.Subtract(one, Abs(Avx.Multiply(Avx.Subtract(lum, half), Vector256.Create(2f)))));

                var toneBoost = Avx.Add(
                    Avx.Add(Avx.Multiply(shadowsBoostV, shadowsWeight), Avx.Multiply(midtonesBoostV, midtonesWeight)),
                    Avx.Multiply(highlightsBoostV, highlightsWeight));

                var boost = Avx.Max(Vector256.Create(0.01f), Avx.Add(one, Avx.Multiply(Avx.Subtract(toneBoost, one), brightnessScaleV)));
                r = Avx.Multiply(r, boost);
                g = Avx.Multiply(g, boost);
                b = Avx.Multiply(b, boost);
            }

            if (this.settings.AdjustContrast)
            {
                r = Avx.Add(Avx.Multiply(Avx.Subtract(r, half), contrastV), half);
                g = Avx.Add(Avx.Multiply(Avx.Subtract(g, half), contrastV), half);
                b = Avx.Add(Avx.Multiply(Avx.Subtract(b, half), contrastV), half);
            }

            if (this.settings.AdjustSaturation)
            {
                var mappedLum = ToneMapperSIMDHelper.Clamp01(
                    Avx.Add(Avx.Add(Avx.Multiply(r, ToneMapperSIMDHelper.Rw), Avx.Multiply(g, ToneMapperSIMDHelper.Gw)), Avx.Multiply(b, ToneMapperSIMDHelper.Bw)));
                var midWeight = ToneMapperSIMDHelper.Clamp01(Avx.Subtract(one, Abs(Avx.Multiply(Avx.Subtract(lum, half), Vector256.Create(2f)))));
                var sat = Avx.Max(zero, Avx.Add(satMinV, Avx.Multiply(satRangeV, midWeight)));

                r = Avx.Add(mappedLum, Avx.Multiply(Avx.Subtract(r, mappedLum), sat));
                g = Avx.Add(mappedLum, Avx.Multiply(Avx.Subtract(g, mappedLum), sat));
                b = Avx.Add(mappedLum, Avx.Multiply(Avx.Subtract(b, mappedLum), sat));
            }

            pixels[0][i] = ToneMapperSIMDHelper.Clamp01(r);
            pixels[1][i] = ToneMapperSIMDHelper.Clamp01(g);
            pixels[2][i] = ToneMapperSIMDHelper.Clamp01(b);
        });

        ApplyGamma(pixels);
    }

    private void ApplyGamma(Vector256<float>[][] pixels)
    {
        var gamma = MathF.Max(this.settings.Gamma, 0.1f);
        if (MathF.Abs(gamma - 1f) <= 1e-3f)
        {
            return;
        }

        var invGamma = Vector256.Create(1f / gamma);
        Parallel.For(0, pixels[0].Length, i =>
        {
            pixels[0][i] = ToneMapperSIMDHelper.Clamp01(ToneMapperSIMDHelper.Pow(pixels[0][i], invGamma));
            pixels[1][i] = ToneMapperSIMDHelper.Clamp01(ToneMapperSIMDHelper.Pow(pixels[1][i], invGamma));
            pixels[2][i] = ToneMapperSIMDHelper.Clamp01(ToneMapperSIMDHelper.Pow(pixels[2][i], invGamma));
        });
    }


    private static Vector256<float> Abs(Vector256<float> value)
    {
        var zero = Vector256<float>.Zero;
        return Avx.Max(value, Avx.Subtract(zero, value));
    }

    private static float ComputeStdDev255(float[] luminance, int count, float mean)
    {
        var varianceSum = 0f;
        for (var i = 0; i < count; i++)
        {
            var d = luminance[i] - mean;
            varianceSum += d * d;
        }

        return MathF.Sqrt(varianceSum / count) * 255f;
    }

    private static float ComputeCorrectionFactor(float value255, float target255)
    {
        var factor = (target255 - value255) / 255f;
        return Math.Clamp(factor, -0.3f, 0.3f);
    }
}
