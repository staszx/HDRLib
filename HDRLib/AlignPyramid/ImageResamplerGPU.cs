// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Align;

using Gpu;
using ILGPU;
using ILGPU.Runtime;
using Interfaces;

internal sealed class ImageResamplerGPU
{
    private readonly GpuContext context;
    private readonly Action<Index1D, ArrayView<byte>, int, int, float, float, float, float, float, float, ArrayView<byte>> resampleKernel;

    public ImageResamplerGPU(GpuContext context)
    {
        this.context = context;
        this.resampleKernel = context.Accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, int, int, float, float, float, float, float, float, ArrayView<byte>>(
            ResampleKernel);
    }

    public IImageProxy Apply(IImageProxy source, AlignmentTransform transform)
    {
        if (Math.Abs(transform.Angle) < 0.001f && transform.X == 0 && transform.Y == 0)
        {
            return source.Clone();
        }

        var width = source.Width;
        var height = source.Height;
        var pixelCount = width * height;
        var sourceRgb = new byte[pixelCount * 3];
        source.LoadFullImage(sourceRgb);

        using var sourceBuffer = this.context.Accelerator.Allocate1D<byte>(sourceRgb.Length);
        using var resultBuffer = this.context.Accelerator.Allocate1D<byte>(sourceRgb.Length);

        sourceBuffer.CopyFromCPU(sourceRgb);

        var centerX = (width - 1) * 0.5f;
        var centerY = (height - 1) * 0.5f;
        var radians = -transform.Angle * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);

        this.resampleKernel(pixelCount, sourceBuffer.View, width, height, centerX, centerY, cos, sin, transform.X, transform.Y, resultBuffer.View);

        var result = source.Clone();
        var resultRgb = resultBuffer.GetAsArray1D();
        result.SaveFullImage(resultRgb);
        return result;
    }

    private static void ResampleKernel(Index1D index, ArrayView<byte> source, int width, int height, float centerX, float centerY, float cos, float sin, float shiftX,
        float shiftY, ArrayView<byte> destination)
    {
        var y = index / width;
        var x = index - y * width;
        var dx = x - centerX - shiftX;
        var dy = y - centerY - shiftY;
        var srcX = cos * dx - sin * dy + centerX;
        var srcY = sin * dx + cos * dy + centerY;
        var destinationOffset = index * 3;

        if (srcX < 0 || srcY < 0 || srcX >= width - 1 || srcY >= height - 1)
        {
            destination[destinationOffset] = 0;
            destination[destinationOffset + 1] = 0;
            destination[destinationOffset + 2] = 0;
            return;
        }

        var x0 = (int)srcX;
        var y0 = (int)srcY;
        var x1 = x0 + 1;
        var y1 = y0 + 1;
        var fx = srcX - x0;
        var fy = srcY - y0;

        var offset00 = (y0 * width + x0) * 3;
        var offset10 = (y0 * width + x1) * 3;
        var offset01 = (y1 * width + x0) * 3;
        var offset11 = (y1 * width + x1) * 3;

        for (var channel = 0; channel < 3; channel++)
        {
            var top = source[offset00 + channel] * (1f - fx) + source[offset10 + channel] * fx;
            var bottom = source[offset01 + channel] * (1f - fx) + source[offset11 + channel] * fx;
            var value = top * (1f - fy) + bottom * fy;
            var rounded = (int)(value + 0.5f);

            if (rounded < 0)
            {
                rounded = 0;
            }
            else if (rounded > 255)
            {
                rounded = 255;
            }

            destination[destinationOffset + channel] = (byte)rounded;
        }
    }
}
