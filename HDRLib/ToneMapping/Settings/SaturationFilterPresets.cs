// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping.Settings;

public static class SaturationFilterPresets
{
    public static SaturationColorFilter CreateSkinFilter(bool enabled = false)
    {
        return new SaturationColorFilter
        {
            Name = "Skin",
            Description = "Reduces saturation in common skin-tone ranges.",
            Enabled = enabled,
            SaturationAdjustment = -30f,
            Ranges =
            [
                new SaturationColorRange
                {
                    HueMin = 0f,
                    HueMax = 50f,
                    SaturationMin = 0.12f,
                    SaturationMax = 0.85f,
                    ValueMin = 0.20f,
                    ValueMax = 1.00f,
                    SaturationMultiplier = -30f
                },
                new SaturationColorRange
                {
                    HueMin = 355f,
                    HueMax = 18f,
                    SaturationMin = 0.10f,
                    SaturationMax = 0.70f,
                    ValueMin = 0.25f,
                    ValueMax = 1.00f,
                    SaturationMultiplier = -30f
                },
                new SaturationColorRange
                {
                    HueMin = 18f,
                    HueMax = 38f,
                    SaturationMin = 0.08f,
                    SaturationMax = 0.65f,
                    ValueMin = 0.18f,
                    ValueMax = 0.95f,
                    SaturationMultiplier = -30f
                }
            ]
        };
    }

    public static SaturationColorFilter CreateGrayFilter(bool enabled = false)
    {
        return new SaturationColorFilter
        {
            Name = "Gray color",
            Description = "Reduces saturation in colors close to neutral gray.",
            Enabled = enabled,
            SaturationAdjustment = -55f,
            Ranges =
            [
                new SaturationColorRange
                {
                    HueMin = 0f,
                    HueMax = 360f,
                    SaturationMin = 0f,
                    SaturationMax = 0.18f,
                    ValueMin = 0.05f,
                    ValueMax = 0.95f,
                    SaturationMultiplier = -55f
                }
            ]
        };
    }
}
