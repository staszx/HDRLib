// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using Image;
using Settings;

internal sealed class AutoAdjustToneMapper : ToneMapper
{
    private readonly AutoAdjustTonemapperSettings settings;

    public AutoAdjustToneMapper(AutoAdjustTonemapperSettings settings) : base(settings)
    {
        this.settings = settings;
    }

    protected override unsafe void ApplyInPlace(Image<Rgb> image, EffectiveToneMapperSettings effectiveSettings)
    {
        var count = image.Length;
        if (count == 0)
        {
            return;
        }

        var pixels = image.Pixels;
        var luminance = new float[count];
        var sumLum = 0f;

        fixed (Rgb* px = pixels)
        fixed (float* lumPtr = luminance)
        {
            for (var i = 0; i < count; i++)
            {
                var lum = Math.Clamp(px[i].Light(), 0f, 1f);
                lumPtr[i] = lum;
                sumLum += lum;
            }

            var avgLum255 = (sumLum / count) * 255f;
            var stdDev255 = ComputeStdDev255(lumPtr, (int)count, avgLum255);

            var exposureScale = MathF.Pow(2f, effectiveSettings.ExposureEV);
            var brightnessScale = exposureScale * effectiveSettings.Brightness;
            if (this.settings.AdjustBrightness)
            {
                var brightnessFactor = ComputeCorrectionFactor(avgLum255, this.settings.TargetLuminance255);
                brightnessScale *= 1f + (MathF.Max(brightnessFactor, 0f) * this.settings.MaskStrengthScale);
            }

            var contrastScale = effectiveSettings.Contrast;
            if (this.settings.AdjustContrast)
            {
                var contrastFactor = ComputeCorrectionFactor(stdDev255, this.settings.TargetContrastStdDev255);
                contrastScale *= (1f + contrastFactor) * 0.9f;
            }

            Parallel.For(0, count, i =>
            {
                var rgb = pixels[i];
                var lum = luminance[i];

                if (this.settings.AdjustBrightness)
                {
                    var toneBoost = ComputeToneBoost(lum, brightnessScale * this.settings.Brightness, this.settings);
                    rgb.Red *= toneBoost;
                    rgb.Green *= toneBoost;
                    rgb.Blue *= toneBoost;
                }

                if (this.settings.AdjustContrast)
                {
                    rgb.Red = ((rgb.Red - 0.5f) * contrastScale) + 0.5f;
                    rgb.Green = ((rgb.Green - 0.5f) * contrastScale) + 0.5f;
                    rgb.Blue = ((rgb.Blue - 0.5f) * contrastScale) + 0.5f;
                }

                if (this.settings.AdjustSaturation)
                {
                    var sat = ComputeSaturation(lum, this.settings) * effectiveSettings.Saturation;
                    var mappedLum = Math.Clamp(rgb.Light(), 0f, 1f);
                    rgb.Red = mappedLum + ((rgb.Red - mappedLum) * sat);
                    rgb.Green = mappedLum + ((rgb.Green - mappedLum) * sat);
                    rgb.Blue = mappedLum + ((rgb.Blue - mappedLum) * sat);
                }

                rgb.Red = Math.Clamp(rgb.Red, 0f, 1f);
                rgb.Green = Math.Clamp(rgb.Green, 0f, 1f);
                rgb.Blue = Math.Clamp(rgb.Blue, 0f, 1f);
                pixels[i] = rgb;
            });

            if (MathF.Abs(effectiveSettings.Gamma - 1f) > 1e-3f)
            {
                var invGamma = 1f / MathF.Max(effectiveSettings.Gamma, 0.1f);
                Parallel.For(0, count, i =>
                {
                    var rgb = pixels[i];
                    rgb.Red = Math.Clamp(MathF.Pow(rgb.Red, invGamma), 0f, 1f);
                    rgb.Green = Math.Clamp(MathF.Pow(rgb.Green, invGamma), 0f, 1f);
                    rgb.Blue = Math.Clamp(MathF.Pow(rgb.Blue, invGamma), 0f, 1f);
                    pixels[i] = rgb;
                });
            }
        }
    }

    private static unsafe float ComputeStdDev255(float* luminance, int count, float avgLum255)
    {
        var mean = avgLum255 / 255f;
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

    private static float ComputeToneBoost(float lum, float brightnessScale, AutoAdjustTonemapperSettings settings)
    {
        var shadowsWeight = Math.Clamp((0.5f - lum) / 0.5f, 0f, 1f);
        var highlightsWeight = Math.Clamp((lum - 0.5f) / 0.5f, 0f, 1f);
        var midtonesWeight = Math.Clamp(1f - MathF.Abs((lum - 0.5f) * 2f), 0f, 1f);

        var toneBoost =
            (settings.ShadowsBoost * shadowsWeight) +
            (settings.MidtonesBoost * midtonesWeight) +
            (settings.HighlightsBoost * highlightsWeight);

        return MathF.Max(0.01f, 1f + ((toneBoost - 1f) * MathF.Max(1f, brightnessScale)));
    }

    private static float ComputeSaturation(float lum, AutoAdjustTonemapperSettings settings)
    {
        var midWeight = Math.Clamp(1f - MathF.Abs((lum - 0.5f) * 2f), 0f, 1f);
        var sat = settings.SaturationMin + ((settings.SaturationMid - settings.SaturationMin) * midWeight);
        return MathF.Max(0f, sat * settings.SaturationStrength);
    }
}
