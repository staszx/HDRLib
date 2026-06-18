// Copyright (c) Stanislav Popov. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Intrinsics;
using System.Text;
using System.Threading.Tasks;

namespace HDRLib.MathUtils
{

    /// <summary>
/// Provides vectorized math functions using AVX instructions.
/// </summary>
public static unsafe class AvxMath
    {

        private const double Ln2 = 0.6931471805599453;
        private const double InvLn2 = 1.4426950408889634;

        private const double C1 = 0.9999964239;
        private const double C2 = -0.4998741238;
        private const double C3 = 0.3317990258;
        private const double C4 = -0.2407338084;
        private const double C5 = 0.1676540711;
        private const double C6 = -0.0953293897;

        private const double E1 = 1.0;
        private const double E2 = 1.0;
        private const double E3 = 0.5;
        private const double E4 = 0.166665703;
        private const double E5 = 0.0416573475;
        private const double E6 = 0.0083013598;

        // 64-bit shift helpers for AVX2
        private static Vector256<long> ShiftLeftLogical64(Vector256<long> v, int bits)
        {
            if (bits == 0) return v;
            var lo = Avx2.ExtractVector128(v, 0);
            var hi = Avx2.ExtractVector128(v, 1);

            lo = Sse2.ShiftLeftLogical(lo, (byte)bits);
            hi = Sse2.ShiftLeftLogical(hi, (byte)bits);

            return Avx.InsertVector128(Avx.InsertVector128(Vector256<long>.Zero, lo, 0), hi, 1);
        }

        private static Vector256<long> ShiftRightLogical64(Vector256<long> v, int bits)
        {
            if (bits == 0) return v;
            var lo = Avx2.ExtractVector128(v, 0);
            var hi = Avx2.ExtractVector128(v, 1);

            lo = Sse2.ShiftRightLogical(lo, (byte)bits);
            hi = Sse2.ShiftRightLogical(hi, (byte)bits);

            return Avx.InsertVector128(Avx.InsertVector128(Vector256<long>.Zero, lo, 0), hi, 1);
        }

        // -------------------- Log(double) --------------------
        /// <summary>
/// Computes the natural logarithm of each element in the vector.
/// </summary>
/// <param name="x">Input vector.</param>
/// <returns>Vector containing the logarithms of the input.</returns>
public static Vector256<double> Log(Vector256<double> x)
        {
            if (!Avx2.IsSupported) throw new PlatformNotSupportedException();

            var one = Vector256.Create(1.0);
            var xi = x.AsInt64();

            // exponent = ((xi >> 52) & 0x7FF) - 1023
            var exponent = ShiftRightLogical64(xi, 52);
            exponent = Avx2.And(exponent, Vector256.Create(0x7FFL));
            exponent = Avx2.Subtract(exponent, Vector256.Create(1023L));

            // mantissaBits = (xi & 0xFFFFFFFFFFFFF) | 0x3FF0000000000000
            var mantissaBits = Avx2.Or(Avx2.And(xi, Vector256.Create(0xFFFFFFFFFFFFFL)), Vector256.Create(0x3FF0000000000000L));
            var m = mantissaBits.AsDouble();

            var f = Avx.Subtract(m, one);

            var p = Vector256.Create(C6);
            p = Avx.Add(Vector256.Create(C5), Avx.Multiply(f, p));
            p = Avx.Add(Vector256.Create(C4), Avx.Multiply(f, p));
            p = Avx.Add(Vector256.Create(C3), Avx.Multiply(f, p));
            p = Avx.Add(Vector256.Create(C2), Avx.Multiply(f, p));
            p = Avx.Add(Vector256.Create(C1), Avx.Multiply(f, p));
            var logm = Avx.Multiply(f, p);

            var e = exponent.AsDouble();
            return Avx.Add(Avx.Multiply(e, Vector256.Create(Ln2)), logm);
        }

        // -------------------- Exp(double) --------------------
        /// <summary>
/// Computes the exponential function (e^x) for each element in the vector.
/// </summary>
/// <param name="x">Input vector.</param>
/// <returns>Vector containing e raised to the power of each input element.</returns>
public static Vector256<double> Exp(Vector256<double> x)
        {
            if (!Avx2.IsSupported) throw new PlatformNotSupportedException();

            var invLn2V = Vector256.Create(InvLn2);
            var ln2V = Vector256.Create(Ln2);

            var n = Avx.RoundToNearestInteger(Avx.Multiply(x, invLn2V)).AsInt64();
            var r = Avx.Subtract(x, Avx.Multiply(n.AsDouble(), ln2V));

            var y = Vector256.Create(E6);
            y = Avx.Add(Vector256.Create(E5), Avx.Multiply(r, y));
            y = Avx.Add(Vector256.Create(E4), Avx.Multiply(r, y));
            y = Avx.Add(Vector256.Create(E3), Avx.Multiply(r, y));
            y = Avx.Add(Vector256.Create(E2), Avx.Multiply(r, y));
            y = Avx.Add(Vector256.Create(E1), Avx.Multiply(r, y));
            var er = y;

            var pow2n = ShiftLeftLogical64(Avx2.Add(n, Vector256.Create(1023L)), 52).AsDouble();
            return Avx.Multiply(er, pow2n);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        /// <summary>
/// Computes the natural logarithm of each element in a float vector.
/// </summary>
/// <param name="value">Input vector.</param>
/// <returns>Vector of logarithms.</returns>
public static Vector256<float> Ln(Vector256<float> value)
        {
            Span<float> lanes = stackalloc float[Vector256<float>.Count];
            value.CopyTo(lanes);
            return Vector256.Create(
                MathF.Log(lanes[0]),
                MathF.Log(lanes[1]),
                MathF.Log(lanes[2]),
                MathF.Log(lanes[3]),
                MathF.Log(lanes[4]),
                MathF.Log(lanes[5]),
                MathF.Log(lanes[6]),
                MathF.Log(lanes[7]));
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        /// <summary>
/// Computes the exponential function (e^x) for each element in a float vector.
/// </summary>
/// <param name="value">Input vector.</param>
/// <returns>Vector of exponentials.</returns>
public static Vector256<float> Exp(Vector256<float> value)
        {
            Span<float> lanes = stackalloc float[Vector256<float>.Count];
            value.CopyTo(lanes);
            return Vector256.Create(
                MathF.Exp(lanes[0]),
                MathF.Exp(lanes[1]),
                MathF.Exp(lanes[2]),
                MathF.Exp(lanes[3]),
                MathF.Exp(lanes[4]),
                MathF.Exp(lanes[5]),
                MathF.Exp(lanes[6]),
                MathF.Exp(lanes[7]));
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        /// <summary>
/// Raises each element of the base vector to the corresponding exponent.
/// </summary>
/// <param name="value">Base values.</param>
/// <param name="exponent">Exponent values.</param>
/// <returns>Vector of results.</returns>
public static Vector256<float> Pow(Vector256<float> value, Vector256<float> exponent)
        {
            return Exp(Avx.Multiply(Ln(value), exponent));
        }
    }




}
