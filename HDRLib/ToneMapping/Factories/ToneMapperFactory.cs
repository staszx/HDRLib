// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping.Factories;

using System;
using Settings;

using HDRLib.Interfaces;

public static class ToneMapperFactory
{
    public static IHdrImageProcessor Create(ToneMapperSettings settings)
    {
        return settings switch
        {
            AcesFilmicTonemapperSettings aces => new AcesFilmicToneMapper(aces),
            NaturalToneMapperSettings naturalSmart => new NaturalToneMapper(naturalSmart),
            AutoAdjustTonemapperSettings autoAdjust => new AutoAdjustToneMapper(autoAdjust),
            ContrastBalancerToneMapperSettings contrastOptimizer => new ContrastBalancerToneMapper(contrastOptimizer),
            BrightnessBalancerToneMapperSettings toneBalancer => new BrightnessBalancerToneMapper(toneBalancer),
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings?.GetType().FullName, "Unknown tone mapper settings type")
        };
    }
}
