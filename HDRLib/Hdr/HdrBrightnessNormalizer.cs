// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Hdr.Debevec;

using HDRLib.Image;

internal static class HdrBrightnessNormalizer
{
    private const float InvByteMax = 1f / 255f;
    private const float MinScale = 1e-6f;

    public static float CalculateTargetAverageBrightness(PixelInfo[] pixelInfo, int width, int height)
    {
        if (pixelInfo.Length == 0)
        {
            return 1f;
        }

        var averageBrightness = new float[pixelInfo.Length];
        Parallel.For(0, pixelInfo.Length, i =>
        {
            var imageBrightnessSum = 0f;
            for (var y = 0; y < height; y++)
            {
                imageBrightnessSum += CalculateBrightnessSum(pixelInfo[i].LoadRow(y), width);
            }

            averageBrightness[i] = imageBrightnessSum / (width * height) * InvByteMax;
        });

        return CalculateAverage(averageBrightness);
    }

    public static float CalculateMaxBrightness(ReadOnlySpan<byte> pixels, int width, int height)
    {
        return CalculateMaxBrightness(pixels, width * height);
    }

    public static float CalculateAverageBrightness(ReadOnlySpan<byte> pixels, int width, int height)
    {
        return CalculateBrightnessSum(pixels, width * height) / (width * height) * InvByteMax;
    }

    public static float CalculateMaxBrightness(Rgb[] pixels, long length)
    {
        var max = 0f;
        for (var i = 0; i < length; i++)
        {
            max = MathF.Max(max, pixels[i].Light());
        }

        return max;
    }

    public static float CalculateMaxChannel(Rgb[] pixels, long length)
    {
        var max = 0f;
        for (var i = 0; i < length; i++)
        {
            var p = pixels[i];
            max = MathF.Max(max, MathF.Max(p.Red, MathF.Max(p.Green, p.Blue)));
        }

        return max;
    }

    public static float CalculateAverageBrightness(Rgb[] pixels, long length)
    {
        var sum = 0f;
        for (var i = 0; i < length; i++)
        {
            sum += pixels[i].Light();
        }

        return length == 0 ? 0f : sum / length;
    }

    public static void AdjustBrightness(Rgb[] pixels, float delta)
    {
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i].Update(
                Clamp01(pixels[i].Red + delta),
                Clamp01(pixels[i].Green + delta),
                Clamp01(pixels[i].Blue + delta));
        }
    }

    public static float CalculateNormalizeScale(float hdrMaxValue)
    {
        return 1f / MathF.Max(hdrMaxValue, MinScale);
    }

    private static float CalculateMaxBrightness(ReadOnlySpan<byte> pixels, int pixelCount)
    {
        var max = 0f;
        for (var i = 0; i < pixelCount; i++)
        {
            var offset = i * Const.ChannelCount;
            max = MathF.Max(max, CalculateBrightness(pixels[offset], pixels[offset + 1], pixels[offset + 2]));
        }

        return max;
    }

    private static float CalculateBrightnessSum(ReadOnlySpan<byte> pixels, int pixelCount)
    {
        var sum = 0f;
        for (var i = 0; i < pixelCount; i++)
        {
            var offset = i * Const.ChannelCount;
            sum += CalculateBrightness(pixels[offset], pixels[offset + 1], pixels[offset + 2]);
        }

        return sum;
    }

    private static float CalculateBrightnessSum(byte[] row, int width)
    {
        var sum = 0f;
        for (var i = 0; i < width; i++)
        {
            var offset = i * Const.ChannelCount;
            sum += CalculateBrightness(row[offset], row[offset + 1], row[offset + 2]);
        }

        return sum;
    }

    private static float CalculateAverage(float[] brightness)
    {
        var sum = 0f;
        for (var i = 0; i < brightness.Length; i++)
        {
            sum += brightness[i];
        }

        return Math.Clamp(sum / brightness.Length, 0f, 1f);
    }

    private static float CalculateBrightness(byte red, byte green, byte blue)
    {
        return (0.2126f * red) + (0.7152f * green) + (0.0722f * blue);
    }

    private static float Clamp01(float value)
    {
        return Math.Clamp(value, 0f, 1f);
    }
}
