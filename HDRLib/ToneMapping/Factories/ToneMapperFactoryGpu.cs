// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping.Factories;

using System;
using HDRLib.Gpu;
using Settings;

internal static class ToneMapperFactoryGpu
{
    public static IToneMapperGpu Create(GpuContext context, ToneMapperSettings settings)
    {
        return settings switch
        {
            AcesFilmicTonemapperSettings aces => new AcesFilmicToneMapperGpu(context, aces),
            NaturalToneMapperSettings naturalSmart => new NaturalToneMapperGpu(context, naturalSmart),
            AutoAdjustTonemapperSettings autoAdjust => new AutoAdjustToneMapperGpu(context, autoAdjust),
            ContrastBalancerToneMapperSettings contrastOptimizer => new ContrastBalancerToneMapperGpu(context, contrastOptimizer),
            BrightnessBalancerToneMapperSettings toneBalancer => new BrightnessBalancerToneMapperGpu(context, toneBalancer),
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings?.GetType().FullName, "Unknown tone mapper settings type")
        };
    }
}
