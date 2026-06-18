// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using HDRLib.Gpu;

internal static class WhiteBalancerFactory
{
    public static WhiteBalancer Create() => new();

    public static WhiteBalancerSIMD CreateSIMD() => new();

    public static WhiteBalancerGpu CreateGpu(GpuContext context) => new(context);
}
