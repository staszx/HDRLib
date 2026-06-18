// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using HDRLib.Image;
using HDRLib.MathUtils;

internal static class ToneMapperSIMDHelper
{
    public static readonly Vector256<float> Zero = Vector256<float>.Zero;
    public static readonly Vector256<float> One = Vector256.Create(1f);
    public static readonly Vector256<float> Half = Vector256.Create(0.5f);
    public static readonly Vector256<float> Epsilon = Vector256.Create(1e-6f);
    public static readonly Vector256<float> Delta = Vector256.Create(1e-4f);
    public static readonly Vector256<float> Rw = Vector256.Create(0.2126f);
    public static readonly Vector256<float> Gw = Vector256.Create(0.7152f);
    public static readonly Vector256<float> Bw = Vector256.Create(0.0722f);

    public static float[] BuildLuminance(Vector256<float>[] r, Vector256<float>[] g, Vector256<float>[] b, int pixelCount)
    {
        var result = new float[pixelCount];
        var vectorLength = r.Length;
        Span<float> lanes = stackalloc float[Vector256<float>.Count];
        for (var i = 0; i < vectorLength; i++)
        {
            var offset = i * Vector256<float>.Count;
            var remaining = pixelCount - offset;
            if (remaining <= 0)
            {
                break;
            }

            var lum = Avx.Add(Avx.Add(Avx.Multiply(r[i], Rw), Avx.Multiply(g[i], Gw)), Avx.Multiply(b[i], Bw));
            if (remaining >= Vector256<float>.Count)
            {
                lum.CopyTo(result.AsSpan(offset, Vector256<float>.Count));
                continue;
            }

            lum.CopyTo(lanes);
            lanes[..remaining].CopyTo(result.AsSpan(offset, remaining));
        }

        return result;
    }

    public static float LogAverage(float[] luminance)
    {
        var len = luminance.Length;
        var vectorCount = len / Vector256<float>.Count;
        var tailStart = vectorCount * Vector256<float>.Count;

        var epsilon = Vector256.Create(1e-4f);
        var sumVec = Vector256<float>.Zero;

        for (var i = 0; i < vectorCount; i++)
        {
            var idx = i * Vector256<float>.Count;
            var lum = Vector256.LoadUnsafe(ref luminance[idx]);
            var logLum = AvxMath.Ln(lum + epsilon);
            sumVec += logLum;
        }

        Span<float> lanes = stackalloc float[Vector256<float>.Count];
        sumVec.CopyTo(lanes);

        var sum = 0f;
        for (var lane = 0; lane < lanes.Length; lane++)
        {
            sum += lanes[lane];
        }

        for (var i = tailStart; i < len; i++)
        {
            sum += MathF.Log(1e-4f + luminance[i]);
        }

        return MathF.Exp(sum / len);
    }

    public static Vector256<float> Pow(Vector256<float> value, Vector256<float> exponent)
    {
        return AvxMath.Pow(value, exponent);
    }

    public static Vector256<float> Clamp01(Vector256<float> value)
    {
        return Avx.Min(One, Avx.Max(Zero, value));
    }

    public static Vector256<float> Lerp(Vector256<float> from, Vector256<float> to, Vector256<float> amount)
    {
        return Avx.Add(from, Avx.Multiply(Avx.Subtract(to, from), amount));
    }

    public static float Percentile(float[] arr, float p)
    {
        var n = arr.Length;
        if (n == 0)
        {
            return 0;
        }

        var pos = p * (n - 1);
        var idx = (int)pos;
        var frac = pos - idx;
        if (idx >= n - 1)
        {
            return arr[n - 1];
        }

        return arr[idx] * (1 - frac) + arr[idx + 1] * frac;
    }

    public static unsafe Image<Rgb> ToImage(Vector256<float>[][] pixels, int width, int height)
    {
        var image = new Image<Rgb>(width, height)
        {
            Width = width,
            Height = height
        };
        var pixelCount = image.Pixels.Length;
        for (var i = 0; i < pixelCount; i++)
        {
            var vectorIndex = i / Vector256<float>.Count;
            var lane = i % Vector256<float>.Count;
            image.Pixels[i] = new Rgb(
                pixels[0][vectorIndex][lane],
                pixels[1][vectorIndex][lane],
                pixels[2][vectorIndex][lane]);
        }

        return image;
    }

    public static void FromImage(Image<Rgb> image, Vector256<float>[][] pixels)
    {
        var pixelCount = image.Pixels.Length;
        var vectorCount = (pixelCount + Vector256<float>.Count - 1) / Vector256<float>.Count;
        Span<float> r = stackalloc float[Vector256<float>.Count];
        Span<float> g = stackalloc float[Vector256<float>.Count];
        Span<float> b = stackalloc float[Vector256<float>.Count];

        for (var vectorIndex = 0; vectorIndex < vectorCount; vectorIndex++)
        {
            r.Clear();
            g.Clear();
            b.Clear();

            for (var lane = 0; lane < Vector256<float>.Count; lane++)
            {
                var pixelIndex = (vectorIndex * Vector256<float>.Count) + lane;
                if (pixelIndex >= pixelCount)
                {
                    break;
                }

                var pixel = image.Pixels[pixelIndex];
                r[lane] = pixel.Red;
                g[lane] = pixel.Green;
                b[lane] = pixel.Blue;
            }

            pixels[0][vectorIndex] = Vector256.Create(
                r[0], r[1], r[2], r[3],
                r[4], r[5], r[6], r[7]);
            pixels[1][vectorIndex] = Vector256.Create(
                g[0], g[1], g[2], g[3],
                g[4], g[5], g[6], g[7]);
            pixels[2][vectorIndex] = Vector256.Create(
                b[0], b[1], b[2], b[3],
                b[4], b[5], b[6], b[7]);
        }
    }
}
