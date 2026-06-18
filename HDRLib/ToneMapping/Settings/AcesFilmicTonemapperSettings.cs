// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping.Settings;

public class AcesFilmicTonemapperSettings : ToneMapperSettings
{
    public AcesFilmicTonemapperSettings()
    {
        this.MakeNeutral();
    }

    public float Key { get; set; } = 0.18f;
}
