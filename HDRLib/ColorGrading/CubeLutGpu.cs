// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ColorGrading;

using HDRLib.Gpu;
using HDRLib.Image;
using ILGPU;
using ILGPU.Runtime;

/// <summary>Device-side LUT lookup and interpolation. Owns one uploaded LUT; reuse and dispose before its context.</summary>
public sealed class CubeLutGpu : IDisposable
{
    private readonly GpuContext context;
    private readonly CubeLut lut;
    private readonly MemoryBuffer1D<Rgb, Stride1D.Dense> table;
    private readonly Action<Index1D, ArrayView<Rgb>, ArrayView<Rgb>, Rgb, Rgb, int, int, float, float, int> kernel;
    private bool disposed;

    /// <summary>Compiles the kernel and uploads the immutable table once.</summary>
    public CubeLutGpu(GpuContext context, CubeLut lut)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(lut);
        this.context = context;
        this.lut = lut;
        this.kernel = context.Accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<Rgb>, ArrayView<Rgb>, Rgb, Rgb, int, int, float, float, int>(ApplyKernel);
        this.table = context.Accelerator.Allocate1D(lut.Values);
    }

    /// <summary>Applies to an existing device RGB buffer without a CPU round trip.
    /// quantizeInput preserves the encoded-byte HDR output contract before LUT application.</summary>
    public void ApplyInPlace(ArrayView1D<Rgb, Stride1D.Dense> pixels, float strength = 1f,
        float pixelScale = 1f, bool quantizeInput = false)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        CubeLut.ValidateStrength(strength);
        if (!float.IsFinite(pixelScale) || pixelScale <= 0) throw new ArgumentOutOfRangeException(nameof(pixelScale));
        if (pixels.Length == 0 || strength == 0) return;
        this.kernel(checked((int)pixels.Length), pixels.BaseView, this.table.View.BaseView,
            this.lut.DomainMin, this.lut.DomainMax, this.lut.Size, this.lut.Is3D ? 1 : 0,
            strength, pixelScale, quantizeInput ? 1 : 0);
        this.context.Accelerator.Synchronize();
    }

    /// <summary>Releases the uploaded LUT; does not dispose the context or caller's image buffer.</summary>
    public void Dispose()
    {
        if (this.disposed) return;
        this.table.Dispose();
        this.disposed = true;
    }

    private static void ApplyKernel(Index1D index, ArrayView<Rgb> pixels, ArrayView<Rgb> table, Rgb min, Rgb max, int size, int is3D, float strength, float scale, int quantizeInput)
    {
        var source = pixels[index];
        if (quantizeInput != 0)
            source = new Rgb(RoundByte(source.Red), RoundByte(source.Green), RoundByte(source.Blue));
        source = new Rgb(Divide(source.Red, scale), Divide(source.Green, scale), Divide(source.Blue, scale));
        var r = Coordinate(source.Red, min.Red, max.Red, size);
        var g = Coordinate(source.Green, min.Green, max.Green, size);
        var b = Coordinate(source.Blue, min.Blue, max.Blue, size);
        var r0 = Math.Min((int)r, size - 2);
        var g0 = Math.Min((int)g, size - 2);
        var b0 = Math.Min((int)b, size - 2);
        r -= r0; g -= g0; b -= b0;
        Rgb mapped;
        if (is3D == 0)
        {
            mapped = new Rgb(Lerp(table[r0].Red, table[r0 + 1].Red, r),
                Lerp(table[g0].Green, table[g0 + 1].Green, g),
                Lerp(table[b0].Blue, table[b0 + 1].Blue, b));
        }
        else
        {
            var stride = size;
            var plane = stride * stride;
            var i = r0 + stride * g0 + plane * b0;
            mapped = Mix(
                Mix(Mix(table[i], table[i + 1], r), Mix(table[i + stride], table[i + stride + 1], r), g),
                Mix(Mix(table[i + plane], table[i + plane + 1], r), Mix(table[i + plane + stride], table[i + plane + stride + 1], r), g), b);
        }

        pixels[index] = Mix(source, mapped, strength) * scale;
    }

    private static float RoundByte(float value)
    {
        if (!(value > 0)) return 0;
        if (value >= 255) return 255;
        var integer = (int)value;
        var fraction = value - integer;
        return integer + (fraction > 0.5f || fraction == 0.5f && (integer & 1) != 0 ? 1 : 0);
    }

    private static float Coordinate(float value, float min, float max, int size)
    {
        if (float.IsNaN(value)) value = min;
        var range = AsDouble(max) - AsDouble(min);
        var delta = AsDouble(value) - AsDouble(min);
        if (delta < 0) delta = 0;
        if (delta > range) delta = range;
        return (float)(delta / range * (size - 1));
    }

    // Some OpenCL devices flush float subnormals during arithmetic/conversion.
    // Decode/encode their bits so tiny input domains still map to the correct LUT coordinates.
    private static double AsDouble(float value)
    {
        var bits = BitConverter.SingleToInt32Bits(value);
        if ((bits & 0x7f800000) != 0) return value;
        var magnitude = (bits & 0x7fffff) * 1.4012984643248171e-45;
        return bits < 0 ? -magnitude : magnitude;
    }

    private static float FromDouble(double value)
    {
        var magnitude = value < 0 ? -value : value;
        if (!(magnitude < 1.1754943508222875e-38)) return (float)value;
        var scaled = magnitude / 1.4012984643248171e-45;
        var bits = (int)scaled;
        var fraction = scaled - bits;
        if (fraction > 0.5 || fraction == 0.5 && (bits & 1) != 0) bits++;
        if (value < 0) bits |= int.MinValue;
        return BitConverter.Int32BitsToSingle(bits);
    }

    private static float Divide(float value, float scale) => scale == 1f ? value : FromDouble(AsDouble(value) / AsDouble(scale));
    private static float Lerp(float a, float b, float t) => FromDouble(AsDouble(a) * (1d - t) + AsDouble(b) * t);
    private static Rgb Mix(Rgb a, Rgb b, float t) => new(Lerp(a.Red, b.Red, t), Lerp(a.Green, b.Green, t), Lerp(a.Blue, b.Blue, t));
}
