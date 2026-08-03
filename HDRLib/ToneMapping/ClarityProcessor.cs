// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using Image;

internal static class ClarityProcessor
{
    private const float Epsilon = 1e-6f;

    public static void ApplyInPlace(Image<Rgb> image, float amount)
    {
        if (image.Length == 0 || image.Width <= 0 || image.Height <= 0)
        {
            return;
        }

        ApplyInPlace(image.Pixels, image.Width, image.Height, amount);
    }

    public static void ApplyInPlace(Rgb[] pixels, int width, int height, float amount)
    {
        var strength = Math.Clamp(amount / 100f, -1f, 1f);
        if (MathF.Abs(strength) <= Epsilon || pixels.Length == 0 || width <= 0 || height <= 0)
        {
            return;
        }

        var radius = ResolveRadius(width, height);
        var source = (Rgb[])pixels.Clone();
        var horizontalSums = new Rgb[pixels.Length];

        Parallel.For(0, height, y =>
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var sumR = 0f;
                var sumG = 0f;
                var sumB = 0f;
                for (var xx = Math.Max(0, x - radius); xx <= Math.Min(width - 1, x + radius); xx++)
                {
                    var sample = source[row + xx];
                    sumR += sample.Red;
                    sumG += sample.Green;
                    sumB += sample.Blue;
                }

                horizontalSums[row + x] = new Rgb(sumR, sumG, sumB);
            }
        });

        Parallel.For(0, height, y =>
        {
            var row = y * width;
            var yFrom = Math.Max(0, y - radius);
            var yTo = Math.Min(height - 1, y + radius);
            var verticalSamples = yTo - yFrom + 1;
            for (var x = 0; x < width; x++)
            {
                var sumR = 0f;
                var sumG = 0f;
                var sumB = 0f;
                for (var yy = yFrom; yy <= yTo; yy++)
                {
                    var sample = horizontalSums[(yy * width) + x];
                    sumR += sample.Red;
                    sumG += sample.Green;
                    sumB += sample.Blue;
                }

                var xFrom = Math.Max(0, x - radius);
                var xTo = Math.Min(width - 1, x + radius);
                var invSamples = 1f / ((xTo - xFrom + 1) * verticalSamples);
                var blur = new Rgb(sumR * invSamples, sumG * invSamples, sumB * invSamples);
                var index = row + x;
                var pixel = source[index];
                var luminance = Math.Clamp(pixel.Light(), 0f, 1f);
                var midtoneWeight = Math.Clamp(1f - (MathF.Abs(luminance - 0.5f) * 2f), 0f, 1f);
                var weightedStrength = strength * midtoneWeight;
                pixels[index] = new Rgb(
                    Math.Clamp(pixel.Red + ((pixel.Red - blur.Red) * weightedStrength), 0f, 1f),
                    Math.Clamp(pixel.Green + ((pixel.Green - blur.Green) * weightedStrength), 0f, 1f),
                    Math.Clamp(pixel.Blue + ((pixel.Blue - blur.Blue) * weightedStrength), 0f, 1f));
            }
        });
    }

    internal static int ResolveRadius(int width, int height)
    {
        return Math.Clamp(Math.Min(width, height) / 64, 1, 20);
    }
}
