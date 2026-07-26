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

    public void ApplyInPlace(
        Image<Rgb> image,
        WhiteBalanceReferenceType referenceType,
        Rgb referenceColor,
        bool preserveHdrRange = false)
    {
        var pixels = image.Pixels;
        if (pixels.Length == 0)
        {
            return;
        }

        var eps = 1e-6f;
        var (scaleR, scaleG, scaleB) = GetScaleFactors(pixels, referenceType, referenceColor, eps);

        var maxValue = preserveHdrRange ? float.MaxValue : 1f;
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i].Update(
                Math.Clamp(pixels[i].Red * scaleR, 0f, maxValue),
                Math.Clamp(pixels[i].Green * scaleG, 0f, maxValue),
                Math.Clamp(pixels[i].Blue * scaleB, 0f, maxValue));
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
            var validCount = 0;
            for (var i = 0; i < pixels.Length; i++)
            {
                var pixel = pixels[i];
                if (!float.IsFinite(pixel.Red) ||
                    !float.IsFinite(pixel.Green) ||
                    !float.IsFinite(pixel.Blue))
                {
                    continue;
                }

                sumR += pixel.Red;
                sumG += pixel.Green;
                sumB += pixel.Blue;
                validCount++;
            }

            if (validCount == 0)
            {
                return (1f, 1f, 1f);
            }

            var avgR = (float)(sumR / validCount);
            var avgG = (float)(sumG / validCount);
            var avgB = (float)(sumB / validCount);
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
