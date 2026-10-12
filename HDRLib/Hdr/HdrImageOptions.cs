// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib;

using ToneMapping.Settings;

public class HdrImageOptions
{
    /// <summary>Optional LUT applied to the output after tone mapping and camera-response encoding.</summary>
    public HDRLib.ColorGrading.CubeLut? OutputLut { get; set; }
    /// <summary>LUT blend amount in [0, 1].</summary>
    public float OutputLutStrength { get; set; } = 1f;
    public int SmoothFactor { get; set; } = 500;
    public int SampleCount { get; set; } = 500;
    public int MotionFilterStrength { get; set; } = 50;
    public bool AutoChannelBalance { get; set; } = false;
    public float ChannelBalanceStrength { get; set; } = 0.6f;
    public float ChannelBalanceMaxGain { get; set; } = 1.8f;
    public string? SaturationFilterPresetsDirectory { get; set; }
    public ToneMapperSettings? ToneMapperSettings { get; set; }
}
