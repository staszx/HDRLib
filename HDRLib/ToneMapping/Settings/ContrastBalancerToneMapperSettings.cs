// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping.Settings;

public sealed class ContrastBalancerToneMapperSettings : ClippedToneMapperSettings
{
    public ContrastBalancerToneMapperSettings()
    {
        this.MakeNeutral();
    }

    public float Strength { get; set; } = 0.7f;
    public float ToneCompression { get; set; } = 0.6f;
    public float LightingEffect { get; set; } = 1.0f;
    public float Luminance { get; set; } = 1.0f;
}
