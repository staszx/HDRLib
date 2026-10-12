// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Hdr.Debevec;

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

internal sealed class CameraResponseEncoderSIMD
{
    private readonly CameraResponseEncoder scalar;
    private readonly bool normalized;
    private readonly float[] lookup;

    public CameraResponseEncoderSIMD(CameraResponseEncoder encoder, bool normalized)
    {
        this.scalar = encoder;
        this.normalized = normalized;
        this.lookup = encoder.CreateLookup(normalized);
    }

    public void ApplyInPlace(Span<float> values, int channel)
    {
        var i = 0;
        if (Avx2.IsSupported)
        {
            for (; i <= values.Length - 8; i += 8)
            {
                this.Encode(Vector256.Create((ReadOnlySpan<float>)values.Slice(i, 8)), channel).CopyTo(values.Slice(i, 8));
            }
        }
        for (; i < values.Length; i++)
        {
            values[i] = this.normalized ? this.scalar.EncodeNormalized(values[i], channel) : this.scalar.Encode(values[i], channel);
        }
    }

    public unsafe Vector256<float> Encode(Vector256<float> input, int channel)
    {
        if ((uint)channel >= 3u) throw new ArgumentOutOfRangeException(nameof(channel));
        var nan = Avx.CompareUnordered(input, input);
        if (this.normalized && Avx.MoveMask(nan) != 0) throw new ArgumentOutOfRangeException("linearValue");
        var zero = Vector256<float>.Zero;
        var one = Vector256.Create(1f);
        if (this.normalized && this.lookup[768 + channel] != 0)
        {
            return Avx.Min(one, Avx.Max(zero, input));
        }

        var value = LogPositive(Avx.Max(input, Vector256.Create(float.Epsilon)));
        var low = Vector256<int>.Zero;
        var high = Vector256.Create(255);
        Vector256<float> result;
        fixed (float* table = &this.lookup[channel * 256])
        {
            // Eight masked bisections cover every interval in a 256-entry LUT.
            for (var step = 0; step < 8; step++)
            {
                var middle = Avx2.ShiftRightLogical(Avx2.Add(low, high), 1);
                var sample = Avx2.GatherVector256(table, middle, 4);
                var active = Avx2.CompareGreaterThan(Avx2.Subtract(high, low), Vector256.Create(1));
                var lower = Avx.And(Avx.CompareLessThan(sample, value), active.AsSingle());
                var upper = Avx.AndNot(lower, active.AsSingle());
                low = Avx.BlendVariable(low.AsSingle(), middle.AsSingle(), lower).AsInt32();
                high = Avx.BlendVariable(high.AsSingle(), middle.AsSingle(), upper).AsInt32();
            }

            var left = Avx2.GatherVector256(table, low, 4);
            var interval = Avx.Subtract(Avx2.GatherVector256(table, high, 4), left);
            var valid = Avx.CompareGreaterThan(interval, zero);
            var divisor = Avx.BlendVariable(one, interval, valid);
            var fraction = Avx.And(Avx.Divide(Avx.Subtract(value, left), divisor), valid);
            result = Avx.Add(Avx.ConvertToVector256Single(low), fraction);
            result = Avx.BlendVariable(result, Vector256.Create(255f), Avx.CompareGreaterThanOrEqual(value, Vector256.Create(table[255])));
            result = Avx.BlendVariable(result, zero, Avx.Or(Avx.CompareLessThanOrEqual(input, zero), Avx.CompareLessThanOrEqual(value, Vector256.Create(table[0]))));
        }

        if (this.normalized)
        {
            result = Avx.Divide(result, Vector256.Create(255f));
            result = Avx.BlendVariable(result, one, Avx.CompareGreaterThanOrEqual(input, one));
        }
        else
        {
            result = Avx.BlendVariable(result, Vector256.Create(255f), Avx.CompareEqual(input, Vector256.Create(float.PositiveInfinity)));
            result = Avx.BlendVariable(result, input, nan);
        }
        return result;
    }

    private static Vector256<float> LogPositive(Vector256<float> input)
    {
        // ln(m) = 2 * (t + t^3/3 + ... + t^13/13), t=(m-1)/(m+1).
        // For m in [1,2), |t| <= 1/3. Normalize subnormals before extracting bits.
        var subnormal = Avx.CompareLessThan(input, Vector256.Create(1.17549435e-38f));
        var x = Avx.BlendVariable(input, Avx.Multiply(input, Vector256.Create(8388608f)), subnormal);
        var bits = x.AsInt32();
        var exponent = Avx2.Subtract(Avx2.ShiftRightLogical(bits, 23), Vector256.Create(127));
        exponent = Avx2.Subtract(exponent, Avx2.And(subnormal.AsInt32(), Vector256.Create(23)));
        var mantissa = Avx2.Or(Avx2.And(bits, Vector256.Create(0x7fffff)), Vector256.Create(0x3f800000)).AsSingle();
        var one = Vector256.Create(1f);
        var t = Avx.Divide(Avx.Subtract(mantissa, one), Avx.Add(mantissa, one));
        var t2 = Avx.Multiply(t, t);
        var p = Vector256.Create(2f / 13f);
        p = Avx.Add(Vector256.Create(2f / 11f), Avx.Multiply(t2, p));
        p = Avx.Add(Vector256.Create(2f / 9f), Avx.Multiply(t2, p));
        p = Avx.Add(Vector256.Create(2f / 7f), Avx.Multiply(t2, p));
        p = Avx.Add(Vector256.Create(2f / 5f), Avx.Multiply(t2, p));
        p = Avx.Add(Vector256.Create(2f / 3f), Avx.Multiply(t2, p));
        p = Avx.Add(Vector256.Create(2f), Avx.Multiply(t2, p));
        return Avx.Add(Avx.Multiply(Avx.ConvertToVector256Single(exponent), Vector256.Create(0.69314718056f)), Avx.Multiply(t, p));
    }
}
