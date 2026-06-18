// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping.Settings;

public sealed class AutoAdjustTonemapperSettings : ToneMapperSettings
{
    public AutoAdjustTonemapperSettings()
    {
        this.MakeNeutral();
    }

    public bool AdjustBrightness { get; set; } = true;
    public bool AdjustContrast { get; set; } = true;
    public bool AdjustSaturation { get; set; } = true;

    public float TargetLuminance255 { get; set; } = 135f;
    public float TargetContrastStdDev255 { get; set; } = 100f;

    public float MaskStrengthScale { get; set; } = 1.1f;

    public float SaturationMin { get; set; } = 0.8f;
    public float SaturationMid { get; set; } = 1.3f;
    public float SaturationStrength { get; set; } = 1.5f;
}
