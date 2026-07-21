// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Gpu;

using ILGPU;
using ILGPU.Runtime;
using Image;

internal class GpuProcessor
{
    #region Fields

    private readonly GpuContext context;
    private readonly Action<Index1D, ArrayView<Rgb>, ArrayView<float>> maxRgbKernel;
    private Action<Index1D, ArrayView<Rgb>, ArrayView<float>> minRgbKernel;
    public Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, Rgb> Multiply;
    public Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>> Copy;
    public Action<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>> Rgb24ToRgb01;


    #endregion

    #region Constructors

    public GpuProcessor(GpuContext context)
    {
        this.context = context;
        this.maxRgbKernel = context.Accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<Rgb>, ArrayView<float>>(MaxRgbKernel);
        this.minRgbKernel = context.Accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<Rgb>, ArrayView<float>>(MinRgbKernel);
        this.Multiply = context.Accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, Rgb>(MultiplyKernel);
        this.Copy = context.Accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>>(CopyKernel);
        this.Rgb24ToRgb01 = context.Accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>>(Rgb24ToRgb01Kernel);
    }

    #endregion

    #region Methods


    public Rgb Max(ArrayView1D<Rgb, Stride1D.Dense> pixels)
    {
        var src = new[] { float.MinValue, float.MinValue, float.MinValue };
        using var result = this.context.Accelerator.Allocate1D<float>(3);
        result.CopyFromCPU(src);


        this.maxRgbKernel((int)pixels.Length, pixels, result.View);
        this.context.Accelerator.Synchronize();

        result.CopyToCPU(src);
        return new Rgb(src[0], src[1], src[2]);
    }

    public Rgb Min(ArrayView1D<Rgb, Stride1D.Dense> pixels)
    {
        var src = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
        using var result = this.context.Accelerator.Allocate1D<float>(3);
        result.CopyFromCPU(src);

        this.minRgbKernel((int)pixels.Length, pixels, result.View);
        this.context.Accelerator.Synchronize();
        result.CopyToCPU(src);
        return new Rgb(src[0], src[1], src[2]);
    }

    private static void MinRgbKernel(Index1D index, ArrayView<Rgb> input, ArrayView<float> min)
    {
        Atomic.Min(ref min[0], input[index].Red);
        Atomic.Min(ref min[1], input[index].Green);
        Atomic.Min(ref min[2], input[index].Blue);
    }

    private static void MaxRgbKernel(Index1D index, ArrayView<Rgb> input, ArrayView<float> max)
    {
        Atomic.Max(ref max[0], input[index].Red);
        Atomic.Max(ref max[1], input[index].Green);
        Atomic.Max(ref max[2], input[index].Blue);
    }

    private static void MultiplyKernel(Index1D index, ArrayView1D<Rgb, Stride1D.Dense> input, Rgb value)
    {
        input[index] *= value;
    }

    private static void CopyKernel(Index1D index, ArrayView1D<Rgb, Stride1D.Dense> source, ArrayView1D<Rgb, Stride1D.Dense> destination)
    {
        destination[index] = source[index];
    }

    private static void Rgb24ToRgb01Kernel(Index1D index, ArrayView1D<byte, Stride1D.Dense> input, ArrayView1D<Rgb, Stride1D.Dense> output)
    {
        var i = (int)index;
        var idx = i * 3;
        output[i] = new Rgb(input[idx] / 255f, input[idx + 1] / 255f, input[idx + 2] / 255f);
    }

    #endregion
}
