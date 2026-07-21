// Copyright (c) Stanislav Popov. All rights reserved.

using HDRLib.Hdr.Debevec;
using ILGPU;
using ILGPU.Runtime;
using HDRLib.Adjust;
using HDRLib.Gpu;
using HDRLib.Interfaces;
using HDRLib.Image;
using HDRLib.ToneMapping;
using HDRLib.ToneMapping.Settings;
using HDRLib.Post;
using HDRLib.PostProcessors;
using ILGPU.Runtime.OpenCL;
using HDRLib.ToneMapping.Factories;

internal class RadianceMapGpu : IRadianceMap, IDisposable
{
    private readonly GpuContext context;
    private readonly ToneMapperSettings? toneMapperSettings;
    
    private ArrayView1D<Rgb, Stride1D.Dense> gpuPixels;
    private int width;
    private int height;
    private float targetAverageBrightness = 1f;
    private Action<Index1D, ArrayView<byte>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<Rgb>, int, int, int, int>
        fillKernel;

    private IToneMapperGpu? toneMapper;

    public RadianceMapGpu(GpuContext context, ToneMapperSettings? toneMapperSettings = null)
    {
        this.context = context;
        this.toneMapperSettings = toneMapperSettings;
        fillKernel = context.Accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<Rgb>, int, int, int, int>(FillKernel);
        toneMapper = this.toneMapperSettings is null ? null : ToneMapperFactoryGpu.Create(this.context, this.toneMapperSettings);
    }

    public void Prepare(int width, int height)
    {
        this.width = width;
        this.height = height;
        int len = width * height;
       // gpuPixels?.Dispose();
        gpuPixels = this.context.Accelerator.Allocate1D<Rgb>(len);
        
        gpuPixels.MemSetToZero();
    }

   

    public unsafe void Fill(PixelInfo[] pixelInfo, double[][] response, float[,] motionMask,  int width, int height)
    {
        Prepare(width, height);
        this.targetAverageBrightness = HdrBrightnessNormalizer.CalculateTargetAverageBrightness(pixelInfo, width, height);
        int pixelCount = width * height;
        int frameSize = pixelCount * 3;
        var imageCount = pixelInfo.Length;
        var accelerator = this.context.Accelerator;

        using var gpuPixelInfo = accelerator.Allocate1D<byte>(imageCount * frameSize);
        for (int i = 0; i < imageCount; i++)
        {
            var frame = pixelInfo[i].LoadFullImage(); // byte[] в RGB interleaved
            if (frame.Length != frameSize)
                throw new Exception($"Frame {i} has invalid size {frame.Length}, expected {frameSize}");
            gpuPixelInfo.View.SubView(i * frameSize, frameSize).CopyFromCPU(frame.ToArray());
        }

        float[] flatResponse = new float[3 * 256];
        for (int c = 0; c < 3; c++)
        for (int j = 0; j < 256; j++)
            flatResponse[c * 256 + j] = (float)response[c][j];

        using var gpuResponse = accelerator.Allocate1D<float>(flatResponse.Length);
        gpuResponse.CopyFromCPU(flatResponse);

        float[] logTimes = pixelInfo.Select(p => (float)p.AvgLuminance).ToArray();
        var fallbackImageIndex = Array.IndexOf(logTimes, logTimes.Min());
        using var gpuLogTimes = accelerator.Allocate1D<float>(logTimes.Length);
        gpuLogTimes.CopyFromCPU(logTimes);

        using var lutW = accelerator.Allocate1D<float>(HDRProcessor<IImageProxy>.LutW.Length);
        lutW.CopyFromCPU(HDRProcessor<IImageProxy>.LutW);

        var flatMotionMask = new float[pixelCount];
        if (motionMask != null)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    flatMotionMask[y * width + x] = motionMask[y, x];
                }
            }
        }
        else
        {
            Array.Fill(flatMotionMask, 1f);
        }

        using var gpuMotionMask = accelerator.Allocate1D<float>(flatMotionMask.Length);
        gpuMotionMask.CopyFromCPU(flatMotionMask);
        fillKernel(pixelCount, gpuPixelInfo.View, gpuResponse.View, gpuLogTimes.View, lutW.View, gpuMotionMask.View, gpuPixels, imageCount, width, height, fallbackImageIndex);
        accelerator.Synchronize();
    }

    public void Normalize(HDRLib.HdrImageOptions options)
    {
        var accelerator = this.context.Accelerator;
        if (this.toneMapperSettings is null)
        {
            var pixels = this.gpuPixels.GetAsArray1D();
            var averageBrightness = HdrBrightnessNormalizer.CalculateAverageBrightness(pixels, pixels.Length);
            var scale= this.targetAverageBrightness / MathF.Max(averageBrightness, 1e-6f) * 255f;
            this.context.Processor.Multiply((int)this.gpuPixels.Length, this.gpuPixels, new Rgb(scale, scale, scale));
            accelerator.Synchronize();
            return;
        }
        this.toneMapper!.ApplyHdrInPlace(this.gpuPixels, this.width, this.height, this.targetAverageBrightness);
        this.context.Processor.Multiply((int)this.gpuPixels.Length, this.gpuPixels, new Rgb(255, 255, 255));
        accelerator.Synchronize();
    }

    static void FillKernel(
        Index1D index,
        ArrayView<byte> images,
        ArrayView<float> response,
        ArrayView<float> logTimes,
        ArrayView<float> lutWeight,
        ArrayView<float> motionMask,
        ArrayView<Rgb> outPixels,
        int imageCount,
        int width,
        int height,
        int fallbackImageIndex)
    {
        int pixelCount = width * height;
        int frameStride = pixelCount * Const.ChannelCount;
        int pixelOffset = index * Const.ChannelCount;
        var motionWeightValue = motionMask[index] > 0.6f ? 1f : 0f;

        var sumW = 0f;
        var idx = 0;
        for (int i = 0; i < imageCount; i++, idx += frameStride)
        {
            var motionWeight = i == 0 ? 1f : motionWeightValue;
            var baseOffset = idx + pixelOffset;
            var red = images[baseOffset];
            var green = images[baseOffset + 1];
            var blue = images[baseOffset + 2];
            var colorWeight = MathF.Min(
                lutWeight[(int)red],
                MathF.Min(
                    lutWeight[(int)green],
                    lutWeight[(int)blue]));
            sumW += colorWeight * motionWeight;
        }

        var finalRed = 0f;
        var finalGreen = 0f;
        var finalBlue = 0f;
        if (sumW > 0)
        {
            idx = 0;
            for (int i = 0; i < imageCount; i++, idx += frameStride)
            {
                var motionWeight = i == 0 ? 1f : motionWeightValue;
                var baseOffset = idx + pixelOffset;
                var red = images[baseOffset];
                var green = images[baseOffset + 1];
                var blue = images[baseOffset + 2];
                var colorWeight = MathF.Min(
                    lutWeight[(int)red],
                    MathF.Min(
                        lutWeight[(int)green],
                        lutWeight[(int)blue]));
                var w = colorWeight * motionWeight / sumW;
                finalRed += (response[(int)red] - logTimes[i]) * w;
                finalGreen += (response[256 + (int)green] - logTimes[i]) * w;
                finalBlue += (response[512 + (int)blue] - logTimes[i]) * w;
            }
        }
        else
        {
            var midOffset = fallbackImageIndex * frameStride + pixelOffset;
            var red = images[midOffset];
            var green = images[midOffset + 1];
            var blue = images[midOffset + 2];
            finalRed = response[(int)red] - logTimes[fallbackImageIndex];
            finalGreen = response[256 + (int)green] - logTimes[fallbackImageIndex];
            finalBlue = response[512 + (int)blue] - logTimes[fallbackImageIndex];
        }

        outPixels[index] = new Rgb(GpuHelper.Exp(finalRed), GpuHelper.Exp(finalGreen), GpuHelper.Exp(finalBlue));
    }


    public unsafe IImageProxy ToImage<T>() where T : IImageProxy
    {
        var image = (IImageProxy)Activator.CreateInstance(typeof(T));
        image.Create(this.width, this.height);

        var pixels = new Rgb[this.gpuPixels.Length];
        this.gpuPixels.CopyToCPU(pixels);

        Parallel.For(0, this.height, y =>
        {
            var row = GC.AllocateUninitializedArray<byte>(this.width * 3);
            var idx = 0;

            fixed (byte* rowP = row)
            {
                var inputIdx = y * this.width;
                for (var x = 0; x < this.width; x++)
                {
                    var pixel = pixels[inputIdx + x];
                    rowP[idx++] = this.Clamp(pixel.Red);
                    rowP[idx++] = this.Clamp(pixel.Green);
                    rowP[idx++] = this.Clamp(pixel.Blue);
                }

                image.SaveRow(y, row);
            }
        });
        return image;
    }

    
    private byte Clamp(double value)
    {
        value = Math.Round(value);
        if (value > 255)
        {
            value = 255;
        }

        if (value < 0)
        {
            value = 0;
        }

        return (byte)value;
    }


    public Rgb[] GetPixels()
    {
        throw new NotImplementedException();
    }

    public void SetPixels(double[][][] pixels)
    {
        throw new NotImplementedException();
    }

    

    public void Dispose()
    {
     //   gpuPixels?.Dispose();

    }
}
