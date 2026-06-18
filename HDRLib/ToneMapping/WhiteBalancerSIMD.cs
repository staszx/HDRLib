// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using HDRLib.Image;

internal sealed class WhiteBalancerSIMD
{
    public void ApplyInPlace(Vector256<float>[][] pixels, int width, int height)
    {
        ApplyInPlace(pixels, width, height, WhiteBalanceReferenceType.Auto, default);
    }

    public void ApplyInPlace(
        Vector256<float>[][] pixels,
        int width,
        int height,
        WhiteBalanceReferenceType referenceType,
        Rgb referenceColor)
    {
        var vectorLength = pixels[0].Length;
        if (vectorLength == 0)
        {
            return;
        }

        var lanes = Vector256<float>.Count;
        var count = vectorLength * lanes;

        var sumR = 0d;
        var sumG = 0d;
        var sumB = 0d;

        for (var i = 0; i < vectorLength; i++)
        {
            var vr = pixels[0][i];
            var vg = pixels[1][i];
            var vb = pixels[2][i];

            for (var lane = 0; lane < lanes; lane++)
            {
                sumR += vr[lane];
                sumG += vg[lane];
                sumB += vb[lane];
            }
        }

        var avgR = (float)(sumR / count);
        var avgG = (float)(sumG / count);
        var avgB = (float)(sumB / count);
        var eps = 1e-6f;
        var sourceR = referenceType == WhiteBalanceReferenceType.Auto ? avgR : referenceColor.Red;
        var sourceG = referenceType == WhiteBalanceReferenceType.Auto ? avgG : referenceColor.Green;
        var sourceB = referenceType == WhiteBalanceReferenceType.Auto ? avgB : referenceColor.Blue;
        var (scaleRScalar, scaleGScalar, scaleBScalar) =
            WhiteBalanceHelper.GetScaleFactors(referenceType, sourceR, sourceG, sourceB, eps);

        var scaleR = Vector256.Create(scaleRScalar);
        var scaleG = Vector256.Create(scaleGScalar);
        var scaleB = Vector256.Create(scaleBScalar);

        Parallel.For(0, vectorLength, i =>
        {
            pixels[0][i] = ToneMapperSIMDHelper.Clamp01(Avx.Multiply(pixels[0][i], scaleR));
            pixels[1][i] = ToneMapperSIMDHelper.Clamp01(Avx.Multiply(pixels[1][i], scaleG));
            pixels[2][i] = ToneMapperSIMDHelper.Clamp01(Avx.Multiply(pixels[2][i], scaleB));
        });
    }
}
