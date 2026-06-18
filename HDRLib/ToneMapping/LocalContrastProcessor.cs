// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using Image;

internal static class LocalContrastProcessor
{
    private const float Epsilon = 1e-6f;

    public static void ApplyInPlace(Image<Rgb> image, float amount, int radius)
    {
        if (image.Length == 0)
        {
            return;
        }

        ApplyInPlace(image.Pixels, image.Width, image.Height, amount, radius);
    }

    public static void ApplyInPlace(Rgb[] pixels, int width, int height, float amount, int radius)
    {
        var strength = Math.Clamp(amount / 100f, -1f, 1f);
        var effectiveRadius = Math.Clamp(radius, 0, 100);
        if (MathF.Abs(strength) <= Epsilon || effectiveRadius <= 0 || pixels.Length == 0 || width <= 0 || height <= 0)
        {
            return;
        }

        var source = new Rgb[pixels.Length];
        Array.Copy(pixels, source, pixels.Length);
        var horizontalSums = new Rgb[pixels.Length];

        Parallel.For(0, height, y =>
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var sumR = 0f;
                var sumG = 0f;
                var sumB = 0f;

                for (var xx = Math.Max(0, x - effectiveRadius); xx <= Math.Min(width - 1, x + effectiveRadius); xx++)
                {
                    var sample = source[row + xx];
                    sumR += sample.Red;
                    sumG += sample.Green;
                    sumB += sample.Blue;
                }

                var index = row + x;
                horizontalSums[index] = new Rgb(sumR, sumG, sumB);
            }
        });

        Parallel.For(0, height, y =>
        {
            var row = y * width;
            var yFrom = Math.Max(0, y - effectiveRadius);
            var yTo = Math.Min(height - 1, y + effectiveRadius);
            var verticalSamples = yTo - yFrom + 1;

            for (var x = 0; x < width; x++)
            {
                var sumR = 0f;
                var sumG = 0f;
                var sumB = 0f;
                var xFrom = Math.Max(0, x - effectiveRadius);
                var xTo = Math.Min(width - 1, x + effectiveRadius);
                var horizontalSamples = xTo - xFrom + 1;

                for (var yy = yFrom; yy <= yTo; yy++)
                {
                    var sample = horizontalSums[(yy * width) + x];
                    sumR += sample.Red;
                    sumG += sample.Green;
                    sumB += sample.Blue;
                }

                var index = row + x;
                var p = source[index];
                var invSamples = 1f / (horizontalSamples * verticalSamples);
                var blurR = sumR * invSamples;
                var blurG = sumG * invSamples;
                var blurB = sumB * invSamples;

                pixels[index] = new Rgb(
                    Math.Clamp(p.Red + ((p.Red - blurR) * strength), 0f, 1f),
                    Math.Clamp(p.Green + ((p.Green - blurG) * strength), 0f, 1f),
                    Math.Clamp(p.Blue + ((p.Blue - blurB) * strength), 0f, 1f));
            }
        });
    }
}
