// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

internal static class ToneMapperHdrExposure
{
    private const float Epsilon = 1e-6f;

    public static float ResolveSceneScale(
        float averageLuminance,
        bool isHdr,
        float sceneAverageBrightness)
    {
        if (!isHdr || !float.IsFinite(sceneAverageBrightness))
        {
            return 1f;
        }

        var sceneTarget = Math.Clamp(sceneAverageBrightness, 0.01f, 0.99f);
        return sceneTarget / MathF.Max(averageLuminance, Epsilon);
    }
}
