// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using Image;

internal static class ToneBoostProcessor
{
    private const float Epsilon = 1e-6f;

    public static bool IsNeutral(float shadowsBoost, float midtonesBoost, float highlightsBoost)
    {
        return MathF.Abs(shadowsBoost - 1f) <= Epsilon &&
               MathF.Abs(midtonesBoost - 1f) <= Epsilon &&
               MathF.Abs(highlightsBoost - 1f) <= Epsilon;
    }

    public static void ApplyInPlace(Rgb[] pixels, float shadowsBoost, float midtonesBoost, float highlightsBoost)
    {
        if (IsNeutral(shadowsBoost, midtonesBoost, highlightsBoost))
        {
            return;
        }

        Parallel.For(0, pixels.Length, i =>
        {
            var rgb = pixels[i];
            var lum = Math.Clamp(rgb.Light(), 0f, 1f);
            var boostedLum = Math.Clamp(lum * ComputeToneBoost(lum, shadowsBoost, midtonesBoost, highlightsBoost), 0f, 1f);
            var scale = boostedLum / MathF.Max(lum, Epsilon);

            pixels[i] = new Rgb(
                Math.Clamp(rgb.Red * scale, 0f, 1f),
                Math.Clamp(rgb.Green * scale, 0f, 1f),
                Math.Clamp(rgb.Blue * scale, 0f, 1f));
        });
    }

    private static float ComputeToneBoost(float value, float shadowsBoost, float midtonesBoost, float highlightsBoost)
    {
        var shadows = Math.Clamp((0.5f - value) / 0.5f, 0f, 1f);
        var highlights = Math.Clamp((value - 0.5f) / 0.5f, 0f, 1f);
        var midtones = Math.Clamp(1f - (MathF.Abs(value - 0.5f) * 2f), 0f, 1f);
        return (shadows * shadowsBoost) + (midtones * midtonesBoost) + (highlights * highlightsBoost);
    }
}
