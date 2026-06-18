// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping.Settings;

public sealed class BrightnessBalancerToneMapperSettings : ClippedToneMapperSettings
{
    public BrightnessBalancerToneMapperSettings()
    {
        this.MakeNeutral();
    }

    public float Strength { get; set; } = 0.75f;
    public float Lighting { get; set; } = 1.0f;
    public float BrightnessBoost { get; set; } = 1.0f;
}
