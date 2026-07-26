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
        Rgb referenceColor,
        bool preserveHdrRange = false)
    {
        var vectorLength = pixels[0].Length;
        var pixelCount = width * height;
        if (vectorLength == 0 || pixelCount == 0)
        {
            return;
        }

        var lanes = Vector256<float>.Count;

        var sumR = 0d;
        var sumG = 0d;
        var sumB = 0d;
        var validCount = 0;

        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            var vectorIndex = pixelIndex / lanes;
            var lane = pixelIndex % lanes;
            var r = pixels[0][vectorIndex][lane];
            var g = pixels[1][vectorIndex][lane];
            var b = pixels[2][vectorIndex][lane];
            if (!float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b))
            {
                continue;
            }

            sumR += r;
            sumG += g;
            sumB += b;
            validCount++;
        }

        var avgR = validCount == 0 ? 0f : (float)(sumR / validCount);
        var avgG = validCount == 0 ? 0f : (float)(sumG / validCount);
        var avgB = validCount == 0 ? 0f : (float)(sumB / validCount);
        var eps = 1e-6f;
        var sourceR = referenceType == WhiteBalanceReferenceType.Auto && validCount != 0 ? avgR : referenceColor.Red;
        var sourceG = referenceType == WhiteBalanceReferenceType.Auto && validCount != 0 ? avgG : referenceColor.Green;
        var sourceB = referenceType == WhiteBalanceReferenceType.Auto && validCount != 0 ? avgB : referenceColor.Blue;
        var (scaleRScalar, scaleGScalar, scaleBScalar) =
            referenceType == WhiteBalanceReferenceType.Auto && validCount == 0
                ? (1f, 1f, 1f)
                : WhiteBalanceHelper.GetScaleFactors(referenceType, sourceR, sourceG, sourceB, eps);

        var scaleR = Vector256.Create(scaleRScalar);
        var scaleG = Vector256.Create(scaleGScalar);
        var scaleB = Vector256.Create(scaleBScalar);
        var zero = Vector256<float>.Zero;
        var maxValue = Vector256.Create(preserveHdrRange ? float.MaxValue : 1f);

        Parallel.For(0, vectorLength, i =>
        {
            pixels[0][i] = Avx.Min(Avx.Max(Avx.Multiply(pixels[0][i], scaleR), zero), maxValue);
            pixels[1][i] = Avx.Min(Avx.Max(Avx.Multiply(pixels[1][i], scaleG), zero), maxValue);
            pixels[2][i] = Avx.Min(Avx.Max(Avx.Multiply(pixels[2][i], scaleB), zero), maxValue);
        });
    }
}
