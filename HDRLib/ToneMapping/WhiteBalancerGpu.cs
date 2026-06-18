// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using HDRLib.Gpu;
using HDRLib.Image;
using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;

internal sealed class WhiteBalancerGpu
{
    private readonly Accelerator accelerator;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float> autoWhiteBalanceKernel;

    public WhiteBalancerGpu(GpuContext context)
    {
        this.accelerator = context.Accelerator;
        this.autoWhiteBalanceKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float>(AutoWhiteBalanceKernel);
    }

    public void ApplyInPlace(ArrayView1D<Rgb, Stride1D.Dense> gpuPixels)
    {
        ApplyInPlace(gpuPixels, WhiteBalanceReferenceType.Auto, default);
    }

    public void ApplyInPlace(
        ArrayView1D<Rgb, Stride1D.Dense> gpuPixels,
        WhiteBalanceReferenceType referenceType,
        Rgb referenceColor)
    {
        var pixels = new Rgb[gpuPixels.Length];
        gpuPixels.CopyToCPU(pixels);

        if (pixels.Length == 0)
        {
            return;
        }

        var sumR = 0d;
        var sumG = 0d;
        var sumB = 0d;
        for (var i = 0; i < pixels.Length; i++)
        {
            sumR += pixels[i].Red;
            sumG += pixels[i].Green;
            sumB += pixels[i].Blue;
        }

        var avgR = (float)(sumR / pixels.Length);
        var avgG = (float)(sumG / pixels.Length);
        var avgB = (float)(sumB / pixels.Length);
        var eps = 1e-6f;
        var sourceR = referenceType == WhiteBalanceReferenceType.Auto ? avgR : referenceColor.Red;
        var sourceG = referenceType == WhiteBalanceReferenceType.Auto ? avgG : referenceColor.Green;
        var sourceB = referenceType == WhiteBalanceReferenceType.Auto ? avgB : referenceColor.Blue;
        var (scaleR, scaleG, scaleB) = WhiteBalanceHelper.GetScaleFactors(referenceType, sourceR, sourceG, sourceB, eps);

        this.autoWhiteBalanceKernel((int)gpuPixels.Length, gpuPixels, scaleR, scaleG, scaleB);
    }

    private static void AutoWhiteBalanceKernel(Index1D index, ArrayView1D<Rgb, Stride1D.Dense> input, float scaleR, float scaleG, float scaleB)
    {
        var pixel = input[index];
        var r = XMath.Clamp(pixel.Red * scaleR, 0f, 1f);
        var g = XMath.Clamp(pixel.Green * scaleG, 0f, 1f);
        var b = XMath.Clamp(pixel.Blue * scaleB, 0f, 1f);
        input[index] = new Rgb(r, g, b);
    }
}
