// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Align;

using ILGPU;
using ILGPU.Runtime;

internal sealed class AlignBitmapLevelGPU : ImageAlignerPyramid.IAlignBitmapLevel
{
    public AlignBitmapLevelGPU(int width, int height, byte medianThreshold, MemoryBuffer1D<byte, Stride1D.Dense> grayscale,
        MemoryBuffer1D<byte, Stride1D.Dense> validityMask, MemoryBuffer1D<byte, Stride1D.Dense> bitmap, MemoryBuffer1D<byte, Stride1D.Dense> mask)
    {
        this.Width = width;
        this.Height = height;
        this.MedianThreshold = medianThreshold;
        this.GpuGrayscale = grayscale;
        this.GpuValidityMask = validityMask;
        this.GpuBitmap = bitmap;
        this.GpuMask = mask;
    }

    public int Width { get; }

    public int Height { get; }

    public byte MedianThreshold { get; }

    public MemoryBuffer1D<byte, Stride1D.Dense> GpuGrayscale { get; }

    public MemoryBuffer1D<byte, Stride1D.Dense> GpuValidityMask { get; }

    public MemoryBuffer1D<byte, Stride1D.Dense> GpuBitmap { get; }

    public MemoryBuffer1D<byte, Stride1D.Dense> GpuMask { get; }

    public void Dispose()
    {
        this.GpuMask.Dispose();
        this.GpuBitmap.Dispose();
        this.GpuValidityMask.Dispose();
        this.GpuGrayscale.Dispose();
    }
}
