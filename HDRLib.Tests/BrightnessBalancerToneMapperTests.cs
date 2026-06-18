// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Tests;

using HDRLib.Gpu;
using HDRLib.Image;
using HDRLib.ToneMapping.Factories;
using HDRLib.ToneMapping.Settings;
using ILGPU.Runtime;
using NUnit.Framework;
using HdrImage = HDRLib.Image.Image<HDRLib.Image.Rgb>;

public class BrightnessBalancerToneMapperTests
{
    [Test]
    public void ApplyInPlace_WhiteClipChangesOutputFromNeutralPreset()
    {
        var source = CreateSampleImage();
        var settings = new BrightnessBalancerToneMapperSettings().MakeNeutral();
        settings.WhiteClip = 0.5f;

        var result = ApplyCpu(settings, Clone(source));

        Assert.That(MeanAbsoluteDifference(source, result), Is.GreaterThan(0.05f));
    }

    [Test]
    public void ApplyInPlace_BlackClipChangesOutputFromNeutralPreset()
    {
        var source = CreateSampleImage();
        var settings = new BrightnessBalancerToneMapperSettings().MakeNeutral();
        settings.BlackClip = 0.7f;

        var result = ApplyCpu(settings, Clone(source));

        Assert.That(MeanAbsoluteDifference(source, result), Is.GreaterThan(0.05f));
    }

    [Test]
    public void ApplyInPlaceGpu_WhiteClipMatchesCpu()
    {
        using var context = CreateGpuContextOrSkip();
        var source = CreateSampleImage();
        var settings = CreateSettings(whiteClip: 0.5f, blackClip: 0.0f);

        var cpu = ApplyCpu(settings, Clone(source));
        var gpu = ApplyGpu(context, settings, Clone(source));

        AssertImagesClose(cpu, gpu, 2e-3f);
    }

    [Test]
    public void ApplyInPlaceGpu_BlackClipMatchesCpu()
    {
        using var context = CreateGpuContextOrSkip();
        var source = CreateSampleImage();
        var settings = CreateSettings(whiteClip: 1.0f, blackClip: 0.7f);

        var cpu = ApplyCpu(settings, Clone(source));
        var gpu = ApplyGpu(context, settings, Clone(source));

        AssertImagesClose(cpu, gpu, 2e-3f);
    }

    private static BrightnessBalancerToneMapperSettings CreateSettings(float whiteClip, float blackClip)
    {
        var settings = new BrightnessBalancerToneMapperSettings().MakeNeutral();
        settings.Strength = 1f;
        settings.WhiteClip = whiteClip;
        settings.BlackClip = blackClip;
        return settings;
    }

    private static HdrImage CreateSampleImage()
    {
        return new HdrImage(4, 1)
        {
            Width = 4,
            Height = 1,
            Pixels =
            [
                new Rgb(0.04f, 0.04f, 0.04f),
                new Rgb(0.18f, 0.16f, 0.14f),
                new Rgb(0.42f, 0.36f, 0.30f),
                new Rgb(0.80f, 0.72f, 0.64f)
            ]
        };
    }

    private static HdrImage ApplyCpu(BrightnessBalancerToneMapperSettings settings, HdrImage image)
    {
        ToneMapperFactory.Create(settings).ApplyInPlace(image);
        return image;
    }

    private static HdrImage ApplyGpu(GpuContext context, BrightnessBalancerToneMapperSettings settings, HdrImage image)
    {
        var mapper = ToneMapperFactoryGpu.Create(context, settings);
        using var pixels = context.Accelerator.Allocate1D<Rgb>(image.Pixels.Length);
        pixels.CopyFromCPU(image.Pixels);
        mapper.ApplyInPlace(pixels.View, image.Width, image.Height);
        image.Pixels = pixels.GetAsArray1D();
        return image;
    }

    private static HdrImage Clone(HdrImage source)
    {
        return new HdrImage(source.Width, source.Height)
        {
            Width = source.Width,
            Height = source.Height,
            Pixels = (Rgb[])source.Pixels.Clone()
        };
    }

    private static float MeanAbsoluteDifference(HdrImage expected, HdrImage actual)
    {
        Assert.That(actual.Pixels, Has.Length.EqualTo(expected.Pixels.Length));
        var total = 0f;
        for (var i = 0; i < expected.Pixels.Length; i++)
        {
            total += MathF.Abs(expected.Pixels[i].Red - actual.Pixels[i].Red);
            total += MathF.Abs(expected.Pixels[i].Green - actual.Pixels[i].Green);
            total += MathF.Abs(expected.Pixels[i].Blue - actual.Pixels[i].Blue);
        }

        return total / (expected.Pixels.Length * 3);
    }

    private static void AssertImagesClose(HdrImage expected, HdrImage actual, float tolerance)
    {
        Assert.That(actual.Pixels, Has.Length.EqualTo(expected.Pixels.Length));
        for (var i = 0; i < expected.Pixels.Length; i++)
        {
            Assert.Multiple(() =>
            {
                Assert.That(actual.Pixels[i].Red, Is.EqualTo(expected.Pixels[i].Red).Within(tolerance), $"Red at {i}");
                Assert.That(actual.Pixels[i].Green, Is.EqualTo(expected.Pixels[i].Green).Within(tolerance), $"Green at {i}");
                Assert.That(actual.Pixels[i].Blue, Is.EqualTo(expected.Pixels[i].Blue).Within(tolerance), $"Blue at {i}");
            });
        }
    }

    private static GpuContext CreateGpuContextOrSkip()
    {
        try
        {
            return new GpuContext();
        }
        catch (Exception ex)
        {
            Assert.Ignore($"GPU accelerator is not available: {ex.Message}");
            throw;
        }
    }
}
