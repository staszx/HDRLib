// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using Image;
using Settings;

internal static class SaturationRangeProcessor
{
    private const float Epsilon = 1e-6f;

    public static void ApplyInPlace(Rgb[] pixels, Rgb[]? sourcePixels, SaturationColorRange[] ranges)
    {
        if (ranges.Length == 0)
        {
            return;
        }

        Parallel.For(0, pixels.Length, i =>
        {
            var mapped = pixels[i];
            var source = sourcePixels is null ? mapped : sourcePixels[i];
            var lum = mapped.Light();
            var saturation = ApplySaturationRanges(1f, source, ranges);

            mapped.Red = lum + ((mapped.Red - lum) * saturation);
            mapped.Green = lum + ((mapped.Green - lum) * saturation);
            mapped.Blue = lum + ((mapped.Blue - lum) * saturation);

            mapped.Red = Math.Clamp(mapped.Red, 0f, 1f);
            mapped.Green = Math.Clamp(mapped.Green, 0f, 1f);
            mapped.Blue = Math.Clamp(mapped.Blue, 0f, 1f);

            pixels[i] = mapped;
        });
    }

    private static float ApplySaturationRanges(float baseSaturation, Rgb rgb, SaturationColorRange[] ranges)
    {
        RgbToHsv(rgb, out var hue, out var saturation, out var value);
        var adjusted = baseSaturation;
        for (var i = 0; i < ranges.Length; i++)
        {
            var strength = ComputeRangeStrength(ranges[i], hue, saturation, value);
            if (strength > 0f)
            {
                adjusted += SaturationAdjustmentToMultiplierDelta(ranges[i].SaturationMultiplier) * strength;
            }
        }

        return MathF.Max(0f, adjusted);
    }

    private static float SaturationAdjustmentToMultiplierDelta(float adjustment)
    {
        var value = Math.Clamp(adjustment, -100f, 100f);
        return value <= 0f
            ? value / 100f
            : value / 50f;
    }

    private static float ComputeRangeStrength(SaturationColorRange range, float hue, float saturation, float value)
    {
        var strength = HueRangeStrength(hue, range.HueMin, range.HueMax);
        strength = MathF.Min(strength, LinearRangeStrength(saturation, range.SaturationMin, range.SaturationMax, 0f, 1f));
        strength = MathF.Min(strength, LinearRangeStrength(value, range.ValueMin, range.ValueMax, 0f, 1f));
        return strength;
    }

    private static float LinearRangeStrength(float value, float min, float max, float domainMin, float domainMax)
    {
        min = Math.Clamp(min, domainMin, domainMax);
        max = Math.Clamp(max, domainMin, domainMax);
        if (max < min)
        {
            (min, max) = (max, min);
        }

        var width = max - min;
        var domainWidth = domainMax - domainMin;
        if (width >= domainWidth - Epsilon)
        {
            return 1f;
        }

        if (width <= Epsilon)
        {
            return MathF.Abs(value - min) <= Epsilon ? 1f : 0f;
        }

        if (value < min || value > max)
        {
            return 0f;
        }

        var distanceToEdge = MathF.Min(value - min, max - value);
        return Math.Clamp((distanceToEdge * 2f) / width, 0f, 1f);
    }

    private static float HueRangeStrength(float hue, float min, float max)
    {
        if (MathF.Abs(max - min) >= 360f - Epsilon)
        {
            return 1f;
        }

        hue = NormalizeHue(hue);
        min = NormalizeHue(min);
        max = NormalizeHue(max);
        var width = max >= min
            ? max - min
            : (360f - min) + max;
        if (width <= Epsilon)
        {
            return MathF.Min(NormalizeHue(hue - min), NormalizeHue(min - hue)) <= Epsilon ? 1f : 0f;
        }

        var offset = NormalizeHue(hue - min);
        if (offset > width)
        {
            return 0f;
        }

        var distanceToEdge = MathF.Min(offset, width - offset);
        return Math.Clamp((distanceToEdge * 2f) / width, 0f, 1f);
    }

    private static float NormalizeHue(float hue)
    {
        hue %= 360f;
        return hue < 0f ? hue + 360f : hue;
    }

    private static void RgbToHsv(Rgb rgb, out float hue, out float saturation, out float value)
    {
        var max = MathF.Max(rgb.Red, MathF.Max(rgb.Green, rgb.Blue));
        var min = MathF.Min(rgb.Red, MathF.Min(rgb.Green, rgb.Blue));
        var delta = max - min;

        value = max;
        saturation = max <= Epsilon ? 0f : delta / max;
        if (delta <= Epsilon)
        {
            hue = 0f;
            return;
        }

        if (MathF.Abs(max - rgb.Red) <= Epsilon)
        {
            hue = 60f * (((rgb.Green - rgb.Blue) / delta) % 6f);
        }
        else if (MathF.Abs(max - rgb.Green) <= Epsilon)
        {
            hue = 60f * (((rgb.Blue - rgb.Red) / delta) + 2f);
        }
        else
        {
            hue = 60f * (((rgb.Red - rgb.Green) / delta) + 4f);
        }

        hue = NormalizeHue(hue);
    }
}
