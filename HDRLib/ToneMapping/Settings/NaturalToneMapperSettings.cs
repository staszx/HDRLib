// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping.Settings;

public sealed class NaturalToneMapperSettings : ToneMapperSettings
{
    public NaturalToneMapperSettings()
    {
        this.MakeNeutral();
    }

    public float TargetGray { get; set; } = 0.22f;
    public float WhitePointPercentile { get; set; } = 0.98f;
    public float OutputMidGray { get; set; } = 0.33f;
    public bool AutoBrightnessCompensation { get; set; } = true;
    public bool BypassToneCompressionForLdr { get; set; } = true;
    public float LdrBypassWhitePointThreshold { get; set; } = 1.2f;
    public float TonalRangeCompression { get; set; } = 4f;
}
