// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Hdr.Debevec;

using Gpu;
using HDRLib.Image;
using HDRLib.Interfaces;
using HDRLib.ToneMapping;
using HDRLib.ToneMapping.Factories;
using HDRLib.ToneMapping.Settings;
using ILGPU;
using ILGPU.Runtime;
using System.Linq;
using System.Runtime.Intrinsics;

public sealed class SingleImageProcessor : IDisposable
{
    private readonly GpuContext? gpuContext;
    private readonly Dictionary<Type, CachedGpuToneMapper> gpuToneMappers = [];
    private MemoryBuffer1D<Rgb, Stride1D.Dense>? gpuSourcePixels;
    private MemoryBuffer1D<Rgb, Stride1D.Dense>? gpuPixels;
    private Rgb[] sourcePixels = Array.Empty<Rgb>();
    private Rgb[] pixels = Array.Empty<Rgb>();
    private int width;
    private int height;

    public SingleImageProcessor(GpuContext? context)
    {
        this.gpuContext = context;
        this.PreloadGpuToneMappers();
    }

    public SingleImageProcessor(IImageProxy source)
    {
        this.LoadSource(source);
    }

    public void LoadSource(IImageProxy source)
    {
        _ = source ?? throw new ArgumentNullException(nameof(source));
        this.ReleaseGpuSource();
        this.width = source.Width;
        this.height = source.Height;

        if (this.gpuContext != null)
        {
            this.LoadSourceGpu(source, this.gpuContext);
            this.sourcePixels = Array.Empty<Rgb>();
            this.pixels = Array.Empty<Rgb>();
            return;
        }

        this.sourcePixels = this.LoadPixels01(source, null);
        this.pixels = (Rgb[])this.sourcePixels.Clone();
    }

    public void Process(ToneMapperSettings toneMapperSettings, GpuContext? context = null)
    {
        if (this.sourcePixels.Length == 0 && this.gpuSourcePixels is null)
        {
            throw new InvalidOperationException("Source image is not loaded.");
        }

        if (toneMapperSettings.IsNeutral())
        {
            this.ProcessNeutral(context ?? this.gpuContext);
            return;
        }

        var effectiveContext = context ?? this.gpuContext;
        if (effectiveContext != null)
        {
            this.ProcessGpu(effectiveContext, toneMapperSettings);
            return;
        }

        this.pixels = (Rgb[])this.sourcePixels.Clone();
        if (SystemHelper.UseAvx)
        {
            this.ProcessSimd(toneMapperSettings);
            return;
        }

        this.ProcessClassic(toneMapperSettings);
    }

    public void Dispose()
    {
        this.ReleaseGpuSource();
        this.gpuToneMappers.Clear();
    }

    public unsafe IImageProxy ToImage<T>() where T : IImageProxy
    {
        if (this.pixels.Length != this.width * this.height)
        {
            throw new InvalidOperationException("Processed image buffer size does not match image dimensions.");
        }

        var image = (IImageProxy?)Activator.CreateInstance(typeof(T))
                    ?? throw new InvalidOperationException($"Cannot create image proxy instance: {typeof(T).FullName}");
        image.Create(this.width, this.height);

        using var handle = new PinnedArray<Rgb>(this.pixels);
        var pxls = handle.Pointer;

        Parallel.For(0, this.height, y =>
        {
            var row = GC.AllocateUninitializedArray<byte>(this.width * Const.ChannelCount);
            var idx = 0;
            var inputIdx = y * this.width;

            fixed (byte* rowP = row)
            {
                for (var x = 0; x < this.width; x++)
                {
                    var pixel = pxls[inputIdx + x];
                    rowP[idx++] = Clamp(pixel.Red);
                    rowP[idx++] = Clamp(pixel.Green);
                    rowP[idx++] = Clamp(pixel.Blue);
                }
            }

            image.SaveRow(y, row);
        });

        return image;
    }

    private Rgb[] LoadPixels01(IImageProxy image, GpuContext? context)
    {
        if (context != null)
        {
            return LoadPixels01Gpu(image, context);
        }

        if (SystemHelper.UseAvx)
        {
            return LoadPixels01Simd(image);
        }

        return LoadPixels01Classic(image);
    }

    private static Rgb[] LoadPixels01Classic(IImageProxy image)
    {
        var width = image.Width;
        var height = image.Height;
        var result = new Rgb[width * height];

        Parallel.For(0, height, y =>
        {
            var row = image.LoadRow(y);
            var rowIdx = 0;
            var dstIdx = y * width;
            for (var x = 0; x < width; x++)
            {
                result[dstIdx + x] = new Rgb(
                    row[rowIdx++] / 255f,
                    row[rowIdx++] / 255f,
                    row[rowIdx++] / 255f);
            }
        });

        return result;
    }

    private static Rgb[] LoadPixels01Simd(IImageProxy image)
    {
        var width = image.Width;
        var height = image.Height;
        var result = new Rgb[width * height];
        var inv255 = Vector256.Create(1f / 255f);
        var vectorSize = Vector256<float>.Count;
        Span<float> r = stackalloc float[Vector256<float>.Count];
        Span<float> g = stackalloc float[Vector256<float>.Count];
        Span<float> b = stackalloc float[Vector256<float>.Count];
        for (var y = 0; y < height; y++)
        {
            var row = image.LoadRow(y);
            var rowBase = y * width;
            var x = 0;
            for (; x + vectorSize <= width; x += vectorSize)
            {
                var offset = x * Const.ChannelCount;
                for (var lane = 0; lane < vectorSize; lane++)
                {
                    var idx = offset + lane * Const.ChannelCount;
                    r[lane] = row[idx];
                    g[lane] = row[idx + 1];
                    b[lane] = row[idx + 2];
                }

                var vr = CreateVector(r) * inv255;
                var vg = CreateVector(g) * inv255;
                var vb = CreateVector(b) * inv255;

                for (var lane = 0; lane < vectorSize; lane++)
                {
                    result[rowBase + x + lane] = new Rgb(vr[lane], vg[lane], vb[lane]);
                }
            }

            var tailOffset = x * Const.ChannelCount;
            for (; x < width; x++, tailOffset += Const.ChannelCount)
            {
                result[rowBase + x] = new Rgb(
                    row[tailOffset] / 255f,
                    row[tailOffset + 1] / 255f,
                    row[tailOffset + 2] / 255f);
            }
        }

        return result;
    }

    private static Rgb[] LoadPixels01Gpu(IImageProxy image, GpuContext context)
    {
        var width = image.Width;
        var height = image.Height;
        var rgbBytes = new byte[width * height * Const.ChannelCount];

        Parallel.For(0, height, y =>
        {
            var row = image.LoadRow(y);
            Buffer.BlockCopy(row, 0, rgbBytes, y * width * Const.ChannelCount, row.Length);
        });

        var accelerator = context.Accelerator;
        using var gpuInput = accelerator.Allocate1D<byte>(rgbBytes.Length);
        using var gpuOutput = accelerator.Allocate1D<Rgb>(width * height);
        gpuInput.CopyFromCPU(rgbBytes);

        var kernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<byte, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>>(LoadPixels01GpuKernel);
        kernel((int)gpuOutput.Length, gpuInput, gpuOutput);
        accelerator.Synchronize();

        var result = new Rgb[width * height];
        gpuOutput.CopyToCPU(result);
        return result;
    }

    private void LoadSourceGpu(IImageProxy image, GpuContext context)
    {
        var width = image.Width;
        var height = image.Height;
        var rgbBytes = new byte[width * height * Const.ChannelCount];

        Parallel.For(0, height, y =>
        {
            var row = image.LoadRow(y);
            Buffer.BlockCopy(row, 0, rgbBytes, y * width * Const.ChannelCount, row.Length);
        });

        var accelerator = context.Accelerator;
        using var gpuInput = accelerator.Allocate1D<byte>(rgbBytes.Length);
        this.gpuSourcePixels = accelerator.Allocate1D<Rgb>(width * height);
        this.gpuPixels = accelerator.Allocate1D<Rgb>(width * height);

        gpuInput.CopyFromCPU(rgbBytes);

        context.Processor.Rgb24ToRgb01((int)this.gpuSourcePixels.Length, gpuInput, this.gpuSourcePixels);
        accelerator.Synchronize();
    }

    private void ProcessClassic(ToneMapperSettings toneMapperSettings)
    {
        var image = new Image<Rgb>(this.width, this.height)
        {
            Pixels = this.pixels
        };

        var toneMapper = ToneMapperFactory.Create(toneMapperSettings);
        toneMapper.ApplyInPlace(image);

        ScaleTo255(image.Pixels);
        this.pixels = image.Pixels;
    }

    private void ProcessSimd(ToneMapperSettings toneMapperSettings)
    {
        var simdPixels = ToSimd(this.pixels);

        var toneMapper = ToneMapperFactorySIMD.Create(toneMapperSettings);
        toneMapper.ApplyInPlace(simdPixels, this.width, this.height);
        if (toneMapperSettings is AcesFilmicTonemapperSettings or NaturalToneMapperSettings or AutoAdjustTonemapperSettings)
        {
            var localContrastPixels = FromSimd(simdPixels, this.width * this.height);
            LocalContrastProcessor.ApplyInPlace(localContrastPixels, this.width, this.height, toneMapperSettings.LocalContrast, toneMapperSettings.LocalContrastRadius);
            simdPixels = ToSimd(localContrastPixels);
        }

        var restored = FromSimd(simdPixels, this.width * this.height);
        ApplyBlending(restored, this.sourcePixels, toneMapperSettings.Transparent);
        ScaleTo255(restored);
        this.pixels = restored;
    }

    private void ProcessGpu(GpuContext context, ToneMapperSettings toneMapperSettings)
    {
        var accelerator = context.Accelerator;
        var toneMapper = this.GetOrCreateGpuToneMapper(context, toneMapperSettings);

        if (this.gpuSourcePixels is null || this.gpuPixels is null)
        {
            this.gpuSourcePixels = accelerator.Allocate1D<Rgb>(this.sourcePixels.Length);
            this.gpuPixels = accelerator.Allocate1D<Rgb>(this.sourcePixels.Length);
            this.gpuSourcePixels.CopyFromCPU(this.sourcePixels);
        }

        context.Processor.Copy((int)this.gpuSourcePixels.Length, this.gpuSourcePixels, this.gpuPixels);

        toneMapper.ApplyInPlace(this.gpuPixels.View, this.width, this.height);
        context.Processor.Multiply((int)this.gpuPixels.Length, this.gpuPixels, new Rgb(255, 255, 255));
        accelerator.Synchronize();

        var result = new Rgb[(int)this.gpuPixels.Length];
        this.gpuPixels.CopyToCPU(result);
        this.pixels = result;
    }

    private void ReleaseGpuSource()
    {
        this.gpuPixels?.Dispose();
        this.gpuSourcePixels?.Dispose();
        this.gpuPixels = null;
        this.gpuSourcePixels = null;
    }

    private IToneMapperGpu GetOrCreateGpuToneMapper(GpuContext context, ToneMapperSettings toneMapperSettings)
    {
        var settingsType = toneMapperSettings.GetType();
        if (this.gpuToneMappers.TryGetValue(settingsType, out var existing))
        {
            CopySettings(toneMapperSettings, existing.Settings);
            return existing.Mapper;
        }

        var cachedSettings = CreateToneMapperSettingsInstance(settingsType);
        CopySettings(toneMapperSettings, cachedSettings);
        var mapper = ToneMapperFactoryGpu.Create(context, cachedSettings);
        this.gpuToneMappers[settingsType] = new CachedGpuToneMapper(mapper, cachedSettings);
        return mapper;
    }

    private void PreloadGpuToneMappers()
    {
        if (this.gpuContext == null)
        {
            return;
        }

        foreach (var settingsType in KnownToneMapperSettingsTypes)
        {
            var settings = CreateToneMapperSettingsInstance(settingsType);
            var mapper = ToneMapperFactoryGpu.Create(this.gpuContext, settings);
            this.gpuToneMappers[settingsType] = new CachedGpuToneMapper(mapper, settings);
        }
    }

    private void ProcessNeutral(GpuContext? context)
    {
        if (this.gpuSourcePixels is not null && context is not null)
        {
            this.pixels = new Rgb[(int)this.gpuSourcePixels.Length];
            this.gpuSourcePixels.CopyToCPU(this.pixels);
        }
        else
        {
            this.pixels = (Rgb[])this.sourcePixels.Clone();
        }

        ScaleTo255(this.pixels);
    }

    private static ToneMapperSettings CreateToneMapperSettingsInstance(Type settingsType)
    {
        return (ToneMapperSettings?)Activator.CreateInstance(settingsType)
               ?? throw new InvalidOperationException($"Cannot create tone mapper settings instance: {settingsType.FullName}");
    }

    private static void CopySettings(ToneMapperSettings source, ToneMapperSettings target)
    {
        var sourceProperties = source.GetType().GetProperties().Where(p => p.CanRead);
        var targetProperties = target.GetType()
            .GetProperties()
            .Where(p => p.CanWrite)
            .ToDictionary(p => p.Name, p => p);

        foreach (var sourceProperty in sourceProperties)
        {
            if (targetProperties.TryGetValue(sourceProperty.Name, out var targetProperty))
            {
                targetProperty.SetValue(target, sourceProperty.GetValue(source));
            }
        }
    }

    private static readonly Type[] KnownToneMapperSettingsTypes =
    [
        typeof(AcesFilmicTonemapperSettings),
        typeof(NaturalToneMapperSettings),
        typeof(AutoAdjustTonemapperSettings),
        typeof(ContrastBalancerToneMapperSettings),
        typeof(BrightnessBalancerToneMapperSettings)
    ];

    private sealed record CachedGpuToneMapper(IToneMapperGpu Mapper, ToneMapperSettings Settings);

    private static unsafe Vector256<float>[][] ToSimd(Rgb[] source)
    {
        var vectorSize = Vector256<float>.Count;
        var vectorLength = (source.Length + vectorSize - 1) / vectorSize;
        Span<float> r = stackalloc float[Vector256<float>.Count];
        Span<float> g = stackalloc float[Vector256<float>.Count];
        Span<float> b = stackalloc float[Vector256<float>.Count];

        var result = new Vector256<float>[Const.ChannelCount][];
        result[0] = GC.AllocateUninitializedArray<Vector256<float>>(vectorLength);
        result[1] = GC.AllocateUninitializedArray<Vector256<float>>(vectorLength);
        result[2] = GC.AllocateUninitializedArray<Vector256<float>>(vectorLength);

        fixed (Rgb* src = source)
        {
            for (var v = 0; v < vectorLength; v++)
            {
                r.Clear();
                g.Clear();
                b.Clear();

                for (var lane = 0; lane < vectorSize; lane++)
                {
                    var index = v * vectorSize + lane;
                    if (index >= source.Length)
                    {
                        break;
                    }

                    var pixel = src[index];
                    r[lane] = pixel.Red;
                    g[lane] = pixel.Green;
                    b[lane] = pixel.Blue;
                }

                result[0][v] = CreateVector(r);
                result[1][v] = CreateVector(g);
                result[2][v] = CreateVector(b);
            }
        }

        return result;
    }

    private static unsafe Rgb[] FromSimd(Vector256<float>[][] source, int pixelCount)
    {
        var result = new Rgb[pixelCount];
        var vectorSize = Vector256<float>.Count;

        fixed (Rgb* dst = result)
        {
            for (var index = 0; index < pixelCount; index++)
            {
                var v = index / vectorSize;
                var lane = index % vectorSize;
                dst[index] = new Rgb(source[0][v][lane], source[1][v][lane], source[2][v][lane]);
            }
        }

        return result;
    }

    private static void ScaleTo255(Rgb[] values)
    {
        Parallel.For(0, values.Length, i =>
        {
            values[i] *= 255f;
        });
    }

    private static void ApplyBlending(Rgb[] pixels, Rgb[] source, float transparent)
    {
        var sourceWeight = Math.Clamp(transparent, 0f, 100f) / 100f;
        if (sourceWeight <= 1e-6f)
        {
            return;
        }

        var resultWeight = 1f - sourceWeight;
        Parallel.For(0, pixels.Length, i =>
        {
            var original = source[i];
            var result = pixels[i];
            pixels[i] = new Rgb(
                (result.Red * resultWeight) + (original.Red * sourceWeight),
                (result.Green * resultWeight) + (original.Green * sourceWeight),
                (result.Blue * resultWeight) + (original.Blue * sourceWeight));
        });
    }

    private static byte Clamp(double value)
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


    private static Vector256<float> CreateVector(Span<float> values)
    {
        return Vector256.Create(
            values[0], values[1], values[2], values[3],
            values[4], values[5], values[6], values[7]);
    }

    private static void LoadPixels01GpuKernel(Index1D index, ArrayView1D<byte, Stride1D.Dense> input, ArrayView1D<Rgb, Stride1D.Dense> output)
    {
        var i = (int)index;
        var idx = i * Const.ChannelCount;
        output[i] = new Rgb(input[idx] / 255f, input[idx + 1] / 255f, input[idx + 2] / 255f);
    }
}
