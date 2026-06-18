// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

internal static class WhiteBalanceHelper
{
    public static (float ScaleR, float ScaleG, float ScaleB) GetScaleFactors(
        WhiteBalanceReferenceType referenceType,
        float sourceR,
        float sourceG,
        float sourceB,
        float eps)
    {
        if (referenceType == WhiteBalanceReferenceType.None)
        {
            return (1f, 1f, 1f);
        }

        if (referenceType == WhiteBalanceReferenceType.Auto)
        {
            var avgGray = (sourceR + sourceG + sourceB) / 3f;
            return
            (
                Math.Clamp(avgGray / Math.Max(sourceR, eps), 0.8f, 1.2f),
                Math.Clamp(avgGray / Math.Max(sourceG, eps), 0.8f, 1.2f),
                Math.Clamp(avgGray / Math.Max(sourceB, eps), 0.8f, 1.2f)
            );
        }

        var targetLevel = referenceType switch
        {
            WhiteBalanceReferenceType.Black => 0.02f,
            WhiteBalanceReferenceType.Gray => 0.5f,
            WhiteBalanceReferenceType.White => 0.98f,
            _ => 0.5f
        };

        return
        (
            Math.Clamp(targetLevel / Math.Max(sourceR, eps), 0.1f, 10f),
            Math.Clamp(targetLevel / Math.Max(sourceG, eps), 0.1f, 10f),
            Math.Clamp(targetLevel / Math.Max(sourceB, eps), 0.1f, 10f)
        );
    }
}
