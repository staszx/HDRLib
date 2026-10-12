// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ColorGrading;

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using HDRLib.Image;
using HDRLib.Interfaces;

/// <summary>AVX2 LUT lookup and interpolation, with a scalar fallback and tail.</summary>
public static class CubeLutSIMD
{
    /// <summary>Applies a LUT to float RGB pixels (pixelScale: 1 or 255).</summary>
    public static void ApplyInPlace(CubeLut lut, Rgb[] pixels, float strength = 1f, float pixelScale = 1f)
    {
        ArgumentNullException.ThrowIfNull(lut);
        ArgumentNullException.ThrowIfNull(pixels);
        CubeLut.ValidateStrength(strength);
        if (!float.IsFinite(pixelScale) || pixelScale <= 0) throw new ArgumentOutOfRangeException(nameof(pixelScale));
        if (strength == 0 || pixels.Length == 0) return;
        if (!Avx2.IsSupported) { lut.ApplyInPlace(pixels, strength, pixelScale); return; }
        const int blockSize = 1024;
        Parallel.For(0, (pixels.Length + blockSize - 1) / blockSize, block =>
            ApplyRange(lut, pixels, block * blockSize, Math.Min(pixels.Length, (block + 1) * blockSize), strength, pixelScale));
    }

    /// <summary>Applies a LUT to an encoded RGB image, preserving byte-conversion semantics.</summary>
    public static void ApplyInPlace(CubeLut lut, IImageProxy image, float strength = 1f)
    {
        ArgumentNullException.ThrowIfNull(lut);
        ArgumentNullException.ThrowIfNull(image);
        CubeLut.ValidateStrength(strength);
        if (strength == 0) return;
        if (!Avx2.IsSupported) { lut.ApplyInPlace(image, strength); return; }
        // IImageProxy does not require implementations to support simultaneous row writes.
        var pixels = new Rgb[image.Width];
        for (var y = 0; y < image.Height; y++)
        {
            var row = image.LoadRow(y);
            for (var x = 0; x < pixels.Length; x++) pixels[x] = new Rgb(row[x * 3], row[x * 3 + 1], row[x * 3 + 2]);
            ApplyRange(lut, pixels, 0, pixels.Length, strength, 255f);
            for (var x = 0; x < pixels.Length; x++)
            {
                row[x * 3] = (byte)Math.Clamp(MathF.Round(pixels[x].Red), 0, 255);
                row[x * 3 + 1] = (byte)Math.Clamp(MathF.Round(pixels[x].Green), 0, 255);
                row[x * 3 + 2] = (byte)Math.Clamp(MathF.Round(pixels[x].Blue), 0, 255);
            }

            image.SaveRow(y, row);
        }
    }

    private static unsafe void ApplyRange(CubeLut lut, Rgb[] pixels, int start, int end, float strength, float pixelScale)
    {
        fixed (Rgb* pixelBuffer = pixels, tableBuffer = lut.Values)
        {
            var source = (float*)pixelBuffer;
            var table = (float*)tableBuffer;
            var offsets = Vector128.Create(0, 3, 6, 9);
            var scale = Vector128.Create(pixelScale);
            var blend = Vector128.Create(strength);
            var i = start;
            for (; i <= end - 4; i += 4)
            {
                var r = Sse.Divide(Avx2.GatherVector128(source + i * 3, offsets, 4), scale);
                var g = Sse.Divide(Avx2.GatherVector128(source + i * 3 + 1, offsets, 4), scale);
                var b = Sse.Divide(Avx2.GatherVector128(source + i * 3 + 2, offsets, 4), scale);
                var rc = Coordinate(r, lut.DomainMin.Red, lut.DomainMax.Red, lut.Size);
                var gc = Coordinate(g, lut.DomainMin.Green, lut.DomainMax.Green, lut.Size);
                var bc = Coordinate(b, lut.DomainMin.Blue, lut.DomainMax.Blue, lut.Size);
                var r0 = Sse41.Min(Sse2.ConvertToVector128Int32WithTruncation(rc), Vector128.Create(lut.Size - 2));
                var g0 = Sse41.Min(Sse2.ConvertToVector128Int32WithTruncation(gc), Vector128.Create(lut.Size - 2));
                var b0 = Sse41.Min(Sse2.ConvertToVector128Int32WithTruncation(bc), Vector128.Create(lut.Size - 2));
                var rw = Sse.Subtract(rc, Sse2.ConvertToVector128Single(r0));
                var gw = Sse.Subtract(gc, Sse2.ConvertToVector128Single(g0));
                var bw = Sse.Subtract(bc, Sse2.ConvertToVector128Single(b0));
                Vector128<float> mappedR, mappedG, mappedB;
                if (lut.Is3D)
                {
                    var index = Sse2.Add(r0, Sse2.Add(Sse41.MultiplyLow(g0, Vector128.Create(lut.Size)),
                        Sse41.MultiplyLow(b0, Vector128.Create(lut.Size * lut.Size))));
                    index = Sse41.MultiplyLow(index, Vector128.Create(3));
                    mappedR = Lookup3D(table, index, rw, gw, bw, lut.Size);
                    mappedG = Lookup3D(table + 1, index, rw, gw, bw, lut.Size);
                    mappedB = Lookup3D(table + 2, index, rw, gw, bw, lut.Size);
                }
                else
                {
                    mappedR = Lookup1D(table, r0, rw);
                    mappedG = Lookup1D(table + 1, g0, gw);
                    mappedB = Lookup1D(table + 2, b0, bw);
                }

                r = Sse.Multiply(Mix(r, mappedR, blend), scale);
                g = Sse.Multiply(Mix(g, mappedG, blend), scale);
                b = Sse.Multiply(Mix(b, mappedB, blend), scale);
                for (var lane = 0; lane < 4; lane++) pixelBuffer[i + lane] = new Rgb(r.GetElement(lane), g.GetElement(lane), b.GetElement(lane));
            }

            for (; i < end; i++)
            {
                var p = pixels[i] / pixelScale;
                pixels[i] = CubeLut.Mix(p, lut.Transform(p), strength) * pixelScale;
            }
        }
    }

    private static Vector128<float> Coordinate(Vector128<float> value, float min, float max, int size)
    {
        value = Sse41.BlendVariable(value, Vector128.Create(min), Sse.CompareUnordered(value, value));
        var delta = Avx.Subtract(Avx.ConvertToVector256Double(value), Vector256.Create((double)min));
        var range = Vector256.Create((double)max - min);
        var clamped = Avx.Min(range, Avx.Max(Vector256<double>.Zero, delta));
        return Avx.ConvertToVector128Single(Avx.Multiply(Avx.Divide(clamped, range), Vector256.Create((double)size - 1)));
    }

    private static unsafe Vector128<float> Lookup1D(float* table, Vector128<int> index, Vector128<float> weight)
    {
        index = Sse41.MultiplyLow(index, Vector128.Create(3));
        return Mix(Avx2.GatherVector128(table, index, 4), Avx2.GatherVector128(table + 3, index, 4), weight);
    }

    private static unsafe Vector128<float> Lookup3D(float* table, Vector128<int> i, Vector128<float> r, Vector128<float> g, Vector128<float> b, int size)
    {
        var stride = size * 3;
        var plane = size * stride;
        return Mix(
            Mix(Mix(Avx2.GatherVector128(table, i, 4), Avx2.GatherVector128(table + 3, i, 4), r),
                Mix(Avx2.GatherVector128(table + stride, i, 4), Avx2.GatherVector128(table + stride + 3, i, 4), r), g),
            Mix(Mix(Avx2.GatherVector128(table + plane, i, 4), Avx2.GatherVector128(table + plane + 3, i, 4), r),
                Mix(Avx2.GatherVector128(table + plane + stride, i, 4), Avx2.GatherVector128(table + plane + stride + 3, i, 4), r), g), b);
    }

    private static Vector128<float> Mix(Vector128<float> a, Vector128<float> b, Vector128<float> t)
    {
        // Match the scalar's double intermediates and float rounding at every interpolation step.
        var weight = Avx.ConvertToVector256Double(t);
        return Avx.ConvertToVector128Single(Avx.Add(
            Avx.Multiply(Avx.ConvertToVector256Double(a), Avx.Subtract(Vector256.Create(1d), weight)),
            Avx.Multiply(Avx.ConvertToVector256Double(b), weight)));
    }
}
