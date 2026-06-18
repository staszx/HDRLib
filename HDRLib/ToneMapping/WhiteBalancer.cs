// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using HDRLib.Image;
using Interfaces;

internal sealed class WhiteBalancer : IHdrImageProcessor
{
    public void ApplyInPlace(Image<Rgb> image)
    {
        ApplyInPlace(image, WhiteBalanceReferenceType.Auto, default);
    }

    public void ApplyInPlace(Image<Rgb> image, WhiteBalanceReferenceType referenceType, Rgb referenceColor)
    {
        var pixels = image.Pixels;
        if (pixels.Length == 0)
        {
            return;
        }

        var eps = 1e-6f;
        var (scaleR, scaleG, scaleB) = GetScaleFactors(pixels, referenceType, referenceColor, eps);

        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i].Update(
                Math.Clamp(pixels[i].Red * scaleR, 0f, 1f),
                Math.Clamp(pixels[i].Green * scaleG, 0f, 1f),
                Math.Clamp(pixels[i].Blue * scaleB, 0f, 1f));
        }
    }

    private static (float ScaleR, float ScaleG, float ScaleB) GetScaleFactors(
        Rgb[] pixels,
        WhiteBalanceReferenceType referenceType,
        Rgb referenceColor,
        float eps)
    {
        if (referenceType == WhiteBalanceReferenceType.Auto)
        {
            var sumR = 0d;
            var sumG = 0d;
            var sumB = 0d;
            for (var i = 0; i < pixels.Length; i++)
            {
                sumR += pixels[i].Red;
                sumG += pixels[i].Green;
                sumB += pixels[i].Blue;
            }

            var avgR = (float)(sumR / pixels.Length);
            var avgG = (float)(sumG / pixels.Length);
            var avgB = (float)(sumB / pixels.Length);
            return WhiteBalanceHelper.GetScaleFactors(referenceType, avgR, avgG, avgB, eps);
        }

        return WhiteBalanceHelper.GetScaleFactors(
            referenceType,
            referenceColor.Red,
            referenceColor.Green,
            referenceColor.Blue,
            eps);
    }
}
