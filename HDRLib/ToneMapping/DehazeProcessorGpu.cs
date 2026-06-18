// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using HDRLib.Gpu;
using HDRLib.Image;
using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;

internal sealed class DehazeProcessorGpu
{
    private const float Epsilon = 1e-6f;

    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float> kernel;

    public DehazeProcessorGpu(GpuContext context)
    {
        this.kernel = context.Accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float>(ApplyKernel);
    }

    public void ApplyInPlace(ArrayView1D<Rgb, Stride1D.Dense> pixels, float amount)
    {
        var strength = Math.Clamp(amount / 100f, 0f, 1f);
        if (strength <= Epsilon)
        {
            return;
        }

        this.kernel((int)pixels.Length, pixels, strength);
    }

    private static void ApplyKernel(Index1D index, ArrayView1D<Rgb, Stride1D.Dense> pixels, float strength)
    {
        var pixel = pixels[index];
        var darkChannel = XMath.Min(pixel.Red, XMath.Min(pixel.Green, pixel.Blue));
        var veil = darkChannel * 0.95f * strength;
        var transmission = XMath.Clamp(1f - veil, 0.35f, 1f);
        var blend = strength * ToneMapperUtilities.SmoothStep(0.02f, 0.35f, darkChannel);

        var red = XMath.Clamp((pixel.Red - veil) / transmission, 0f, 1f);
        var green = XMath.Clamp((pixel.Green - veil) / transmission, 0f, 1f);
        var blue = XMath.Clamp((pixel.Blue - veil) / transmission, 0f, 1f);

        pixels[index] = new Rgb(
            ToneMapperUtilities.Lerp(pixel.Red, red, blend),
            ToneMapperUtilities.Lerp(pixel.Green, green, blend),
            ToneMapperUtilities.Lerp(pixel.Blue, blue, blend));
    }

}
