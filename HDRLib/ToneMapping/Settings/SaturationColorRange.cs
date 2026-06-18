// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping.Settings;

public readonly struct SaturationColorRange
{
    public float HueMin { get; init; }
    public float HueMax { get; init; }
    public float SaturationMin { get; init; }
    public float SaturationMax { get; init; }
    public float ValueMin { get; init; }
    public float ValueMax { get; init; }
    public float SaturationMultiplier { get; init; }
}
