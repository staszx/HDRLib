// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using Settings;

internal static class NaturalToneMapperExposure
{
    public static float ResolveTargetGray(
        NaturalToneMapperSettings settings,
        bool isHdr,
        float sceneAverageBrightness)
    {
        if (!isHdr || !float.IsFinite(sceneAverageBrightness))
        {
            return MathF.Max(settings.TargetGray, 0.01f);
        }

        var sceneTarget = Math.Clamp(sceneAverageBrightness, 0.01f, 0.99f);
        var targetGrayAdjustment = MathF.Max(settings.TargetGray, 0.01f) /
                                   NaturalToneMapperSettings.NeutralTargetGray;
        return Math.Clamp(sceneTarget * targetGrayAdjustment, 0.01f, 0.99f);
    }
}
