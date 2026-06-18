// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

internal static class DehazeProcessorSIMD
{
    private const float Epsilon = 1e-6f;

    public static void ApplyInPlace(Vector256<float>[][] pixels, float amount)
    {
        var strengthValue = Math.Clamp(amount / 100f, 0f, 1f);
        if (strengthValue <= Epsilon)
        {
            return;
        }

        var strength = Vector256.Create(strengthValue);
        var veilScale = Vector256.Create(0.95f);
        var minTransmission = Vector256.Create(0.35f);
        var smoothStart = Vector256.Create(0.02f);
        var smoothRange = Vector256.Create(0.33f);
        var three = Vector256.Create(3f);
        var two = Vector256.Create(2f);

        Parallel.For(0, pixels[0].Length, i =>
        {
            var r = pixels[0][i];
            var g = pixels[1][i];
            var b = pixels[2][i];

            var darkChannel = Avx.Min(r, Avx.Min(g, b));
            var veil = Avx.Multiply(Avx.Multiply(darkChannel, veilScale), strength);
            var transmission = Avx.Max(minTransmission, Avx.Min(ToneMapperSIMDHelper.One, Avx.Subtract(ToneMapperSIMDHelper.One, veil)));

            var recoveredR = ToneMapperSIMDHelper.Clamp01(Avx.Divide(Avx.Subtract(r, veil), transmission));
            var recoveredG = ToneMapperSIMDHelper.Clamp01(Avx.Divide(Avx.Subtract(g, veil), transmission));
            var recoveredB = ToneMapperSIMDHelper.Clamp01(Avx.Divide(Avx.Subtract(b, veil), transmission));

            var t = ToneMapperSIMDHelper.Clamp01(Avx.Divide(Avx.Subtract(darkChannel, smoothStart), smoothRange));
            var smooth = Avx.Multiply(Avx.Multiply(t, t), Avx.Subtract(three, Avx.Multiply(two, t)));
            var blend = Avx.Multiply(strength, smooth);

            pixels[0][i] = ToneMapperSIMDHelper.Lerp(r, recoveredR, blend);
            pixels[1][i] = ToneMapperSIMDHelper.Lerp(g, recoveredG, blend);
            pixels[2][i] = ToneMapperSIMDHelper.Lerp(b, recoveredB, blend);
        });
    }
}
