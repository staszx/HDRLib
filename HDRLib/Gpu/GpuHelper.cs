// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Adjust;

using System.Runtime.CompilerServices;
using ILGPU.Algorithms;

internal class GpuHelper
{
    #region Methods

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Log(float x)
    {
        if (x <= 0f)
        {
            return float.NegativeInfinity;
        }

        var bits = BitConverter.SingleToInt32Bits(x);
        var exponent = ((bits >> 23) & 0xFF) - 127;
        bits = (bits & 0x7FFFFF) | 0x3F800000; // мантисса [1,2)
        var m = BitConverter.Int32BitsToSingle(bits);

        var y = m - 1f;
        var log2m = y * (1.3465558f + y * (-0.4668011f + y * (0.1903190f - 0.0547210f * y)));

        return (log2m + exponent) * 0.69314718056f; // ln(x)
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Exp(float x)
    {
        if (x < -87.33654f)
        {
            return 0f;
        }

        if (x > 88.72284f)
        {
            return float.PositiveInfinity;
        }

        var a = x * 1.4426950408889634f; // x / ln(2)
        var i = (int)MathF.Floor(a);
        var f = a - i;

        var p = 1f + f * (0.69314718056f + f * (0.2402265069f + f * (0.05550410866f + f * 0.009618129108f)));

        var exponent = (i + 127) << 23;
        var pow2i = BitConverter.Int32BitsToSingle(exponent);

        return pow2i * p;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SmoothStep(float edge0, float edge1, float x)
    {
        x = XMath.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Pow(float baseValue, float exponent)
    {
        if (baseValue <= 0.0)
        {
            return 0.0f;
        }

        return Exp(exponent * Log(baseValue));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RoundToInt(float x)
    {
        return x >= 0f ? (int)(x + 0.5f) : (int)(x - 0.5f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long floatToInt64Bits(float d)
    {
        return Unsafe.As<float, long>(ref d);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Int64BitsTofloat(long i)
    {
        return Unsafe.As<long, float>(ref i);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float RemFloat(float x, float y)
    {
        return x - ((int)(x / y)) * y;
    }

    #endregion
}