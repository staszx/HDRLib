// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping.Settings;

public sealed class SaturationColorFilter
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; }
    public float SaturationAdjustment { get; set; }
    public SaturationColorRange[] Ranges { get; set; } = [];
}
