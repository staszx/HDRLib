// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping.Factories;

using System;
using Settings;

internal static class ToneMapperFactorySIMD
{
    public static ToneMapperSIMD Create(ToneMapperSettings settings)
    {
        return settings switch
        {
            AcesFilmicTonemapperSettings aces => new AcesFilmicToneMapperSIMD(aces),
            NaturalToneMapperSettings naturalSmart => new NaturalToneMapperSIMD(naturalSmart),
            ContrastBalancerToneMapperSettings contrastOptimizer => new ContrastBalancerToneMapperSIMD(contrastOptimizer),
            BrightnessBalancerToneMapperSettings toneBalancer => new BrightnessBalancerToneMapperSIMD(toneBalancer),
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings?.GetType().FullName, "Unknown tone mapper settings type")
        };
    }
}
