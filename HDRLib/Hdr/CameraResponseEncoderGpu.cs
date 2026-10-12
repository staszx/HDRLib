// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Hdr.Debevec;

using Gpu;
using Image;
using ILGPU;
using ILGPU.Runtime;

internal sealed class CameraResponseEncoderGpu
{
    private readonly GpuContext context;
    private readonly Action<Index1D, ArrayView<Rgb>, ArrayView<float>, ArrayView<int>, int, float> kernel;

    public CameraResponseEncoderGpu(GpuContext context)
    {
        this.context = context;
        this.kernel = context.Accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<Rgb>, ArrayView<float>, ArrayView<int>, int, float>(EncodeKernel);
    }

    public void ApplyInPlace(ArrayView1D<Rgb, Stride1D.Dense> pixels, CameraResponseEncoder encoder, bool normalized, float outputScale = 1f)
    {
        if (pixels.Length == 0) return;
        using var table = this.context.Accelerator.Allocate1D(encoder.CreateLookup(normalized));
        using var error = this.context.Accelerator.Allocate1D<int>(1);
        error.MemSetToZero();
        this.kernel((int)pixels.Length, pixels.BaseView, table.View, error.View, normalized ? 1 : 0, outputScale);
        this.context.Accelerator.Synchronize();
        // Only the error flag crosses back to the CPU; the RGB buffer stays on GPU.
        if (normalized && error.GetAsArray1D()[0] != 0) throw new ArgumentOutOfRangeException("linearValue");
    }

    private static void EncodeKernel(Index1D index, ArrayView<Rgb> pixels, ArrayView<float> table, ArrayView<int> error, int normalized, float outputScale)
    {
        var p = pixels[index];
        pixels[index] = new Rgb(
            Encode(p.Red, 0, table, error, normalized) * outputScale,
            Encode(p.Green, 1, table, error, normalized) * outputScale,
            Encode(p.Blue, 2, table, error, normalized) * outputScale);
    }

    private static float Encode(float input, int channel, ArrayView<float> table, ArrayView<int> error, int normalized)
    {
        var bits = BitConverter.SingleToInt32Bits(input);
        if ((bits & 0x7fffffff) > 0x7f800000)
        {
            if (normalized != 0) Atomic.Exchange(ref error[0], 1);
            return input;
        }
        // Some OpenCL devices flush subnormal operands during float comparisons.
        // Test the sign/zero bits so positive subnormal radiance reaches the log.
        if (bits <= 0) return 0;
        var offset = channel * 256;
        if (normalized != 0)
        {
            if (input >= 1) return 1;
            if (table[768 + channel] != 0) return input;
        }
        if (bits == 0x7f800000) return normalized != 0 ? 1f : 255f;
        var value = LogPositive(input);
        if (value <= table[offset]) return 0;
        if (value >= table[offset + 255]) return normalized != 0 ? 1f : 255f;

        var low = 0;
        var high = 255;
        while (high - low > 1)
        {
            var middle = (low + high) / 2;
            if (table[offset + middle] < value) low = middle;
            else high = middle;
        }
        var interval = table[offset + high] - table[offset + low];
        var result = interval > 0 ? low + (value - table[offset + low]) / interval : low;
        return normalized != 0 ? result / 255f : result;
    }

    private static float LogPositive(float input)
    {
        // Same mantissa expansion as the AVX2 encoder. Avoid changing the
        // application's ILGPU math configuration just to enable LogF on CUDA.
        var bits = BitConverter.SingleToInt32Bits(input);
        var exponent = (bits >> 23) - 127;
        if ((bits & 0x7f800000) == 0)
        {
            // Normalize the significand with integer shifts; float multiplication
            // would flush the original subnormal to zero on some devices.
            exponent = -126;
            while (bits < 0x800000)
            {
                bits <<= 1;
                exponent--;
            }
        }
        var mantissa = BitConverter.Int32BitsToSingle((bits & 0x7fffff) | 0x3f800000);
        var t = (mantissa - 1f) / (mantissa + 1f);
        var t2 = t * t;
        var p = 2f / 13f;
        p = 2f / 11f + t2 * p;
        p = 2f / 9f + t2 * p;
        p = 2f / 7f + t2 * p;
        p = 2f / 5f + t2 * p;
        p = 2f / 3f + t2 * p;
        p = 2f + t2 * p;
        return exponent * 0.69314718056f + t * p;
    }
}
