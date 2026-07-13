// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using HDRLib.Gpu;
using HDRLib.Image;
using ILGPU;
using ILGPU.Runtime;

internal interface IToneMapperGpu : IDisposable
{
    void ApplyInPlace(ArrayView1D<Rgb, Stride1D.Dense> gpuPixels, int width, int height);

    void ApplyHdrInPlace(ArrayView1D<Rgb, Stride1D.Dense> gpuPixels, int width, int height);
}
