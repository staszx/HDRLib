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
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float, float> autoWhiteBalanceKernel;

    public WhiteBalancerGpu(GpuContext context)
    {
        this.accelerator = context.Accelerator;
        this.autoWhiteBalanceKernel = this.accelerator.LoadAutoGroupedStreamKernel<
            Index1D,
            ArrayView1D<Rgb, Stride1D.Dense>,
            float,
            float,
            float,
            float>(AutoWhiteBalanceKernel);
    }

    public void ApplyInPlace(ArrayView1D<Rgb, Stride1D.Dense> gpuPixels)
    {
        ApplyInPlace(gpuPixels, WhiteBalanceReferenceType.Auto, default);
    }

    public void ApplyInPlace(
        ArrayView1D<Rgb, Stride1D.Dense> gpuPixels,
        WhiteBalanceReferenceType referenceType,
        Rgb referenceColor,
        bool preserveHdrRange = false)
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
        var validCount = 0;
        for (var i = 0; i < pixels.Length; i++)
        {
            var pixel = pixels[i];
            if (!float.IsFinite(pixel.Red) ||
                !float.IsFinite(pixel.Green) ||
                !float.IsFinite(pixel.Blue))
            {
                continue;
            }

            sumR += pixel.Red;
            sumG += pixel.Green;
            sumB += pixel.Blue;
            validCount++;
        }

        var avgR = validCount == 0 ? 0f : (float)(sumR / validCount);
        var avgG = validCount == 0 ? 0f : (float)(sumG / validCount);
        var avgB = validCount == 0 ? 0f : (float)(sumB / validCount);
        var eps = 1e-6f;
        var sourceR = referenceType == WhiteBalanceReferenceType.Auto && validCount != 0 ? avgR : referenceColor.Red;
        var sourceG = referenceType == WhiteBalanceReferenceType.Auto && validCount != 0 ? avgG : referenceColor.Green;
        var sourceB = referenceType == WhiteBalanceReferenceType.Auto && validCount != 0 ? avgB : referenceColor.Blue;
        var (scaleR, scaleG, scaleB) = referenceType == WhiteBalanceReferenceType.Auto && validCount == 0
            ? (1f, 1f, 1f)
            : WhiteBalanceHelper.GetScaleFactors(referenceType, sourceR, sourceG, sourceB, eps);

        this.autoWhiteBalanceKernel(
            (int)gpuPixels.Length,
            gpuPixels,
            scaleR,
            scaleG,
            scaleB,
            preserveHdrRange ? float.MaxValue : 1f);
    }

    private static void AutoWhiteBalanceKernel(
        Index1D index,
        ArrayView1D<Rgb, Stride1D.Dense> input,
        float scaleR,
        float scaleG,
        float scaleB,
        float maxValue)
    {
        var pixel = input[index];
        var r = XMath.Clamp(pixel.Red * scaleR, 0f, maxValue);
        var g = XMath.Clamp(pixel.Green * scaleG, 0f, maxValue);
        var b = XMath.Clamp(pixel.Blue * scaleB, 0f, maxValue);
        input[index] = new Rgb(r, g, b);
    }
}
