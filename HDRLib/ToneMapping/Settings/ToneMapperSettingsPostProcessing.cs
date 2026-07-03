// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping.Settings;

using HDRLib.PostProcessors;

internal static class ToneMapperSettingsPostProcessing
{
    public static PostProcessSettings ToPostProcessSettings(this ToneMapperSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new PostProcessSettings
        {
            Exposure = settings.ExposureEV,
            Brightness = settings.Brightness,
            Contrast = settings.Contrast,
            Vibrance = SaturationToMultiplier(settings.Saturation)
        };
    }

    public static PostProcessSettings WithAutoAdjust(
        this PostProcessSettings settings,
        ImageAdjustSettings auto,
        float contrastMultiplier = 1.0f,
        float vibranceMultiplier = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(auto);

        var autoSettings = auto.ToPostProcessSettings(
            includeToneRegions: true,
            contrastMultiplier: contrastMultiplier,
            vibranceMultiplier: vibranceMultiplier);
        return settings.Combine(autoSettings);
    }

    private static float SaturationToMultiplier(float saturation)
    {
        var value = Math.Clamp(saturation, -100f, 100f);
        return value <= 0f
            ? 1f + (value / 100f)
            : 1f + (value / 50f);
    }
}
