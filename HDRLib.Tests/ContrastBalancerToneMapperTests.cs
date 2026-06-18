// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Tests;

using HDRLib.Gpu;
using HDRLib.Image;
using HDRLib.ToneMapping.Factories;
using HDRLib.ToneMapping.Settings;
using ILGPU.Runtime;
using NUnit.Framework;
using HdrImage = HDRLib.Image.Image<HDRLib.Image.Rgb>;

public class ContrastBalancerToneMapperTests
{
    [Test]
    public void ApplyInPlace_LuminanceChangesOutput()
    {
        var low = ApplyCpu(CreateSettings(luminance: 0.25f, toneCompression: 0.6f, lightingEffect: 1.0f), CreateSampleImage());
        var high = ApplyCpu(CreateSettings(luminance: 2.0f, toneCompression: 0.6f, lightingEffect: 1.0f), CreateSampleImage());

        Assert.That(MeanLuminance(high), Is.GreaterThan(MeanLuminance(low) + 0.05f));
    }

    [Test]
    public void ApplyInPlace_LuminanceChangesOutputFromNeutralPreset()
    {
        var source = CreateSampleImage();
        var settings = new ContrastBalancerToneMapperSettings().MakeNeutral();
        settings.Luminance = 2.0f;

        var result = ApplyCpu(settings, Clone(source));

        Assert.That(MeanAbsoluteDifference(source, result), Is.GreaterThan(0.05f));
    }

    [Test]
    public void ApplyInPlace_ToneCompressionChangesOutput()
    {
        var weak = ApplyCpu(CreateSettings(luminance: 1.0f, toneCompression: 0.2f, lightingEffect: 1.0f), CreateSampleImage());
        var strong = ApplyCpu(CreateSettings(luminance: 1.0f, toneCompression: 2.0f, lightingEffect: 1.0f), CreateSampleImage());

        Assert.That(MeanLuminance(weak), Is.GreaterThan(MeanLuminance(strong) + 0.05f));
    }

    [Test]
    public void ApplyInPlace_ToneCompressionChangesOutputFromNeutralPreset()
    {
        var source = CreateSampleImage();
        var settings = new ContrastBalancerToneMapperSettings().MakeNeutral();
        settings.ToneCompression = 0.2f;

        var result = ApplyCpu(settings, Clone(source));

        Assert.That(MeanAbsoluteDifference(source, result), Is.GreaterThan(0.05f));
    }

    [Test]
    public void ApplyInPlace_LightingEffectChangesOutput()
    {
        var low = ApplyCpu(CreateSettings(luminance: 1.0f, toneCompression: 0.6f, lightingEffect: 0.0f), CreateSampleImage());
        var high = ApplyCpu(CreateSettings(luminance: 1.0f, toneCompression: 0.6f, lightingEffect: 2.0f), CreateSampleImage());

        Assert.That(MeanAbsoluteDifference(low, high), Is.GreaterThan(0.05f));
    }

    [Test]
    public void ApplyInPlace_LightingEffectChangesOutputFromNeutralPreset()
    {
        var source = CreateSampleImage();
        var settings = new ContrastBalancerToneMapperSettings().MakeNeutral();
        settings.LightingEffect = 0.0f;

        var result = ApplyCpu(settings, Clone(source));

        Assert.That(MeanAbsoluteDifference(source, result), Is.GreaterThan(0.05f));
    }

    [Test]
    public void ApplyInPlace_WhiteClipChangesOutput()
    {
        var lowClip = ApplyCpu(CreateSettings(luminance: 1.0f, toneCompression: 0.6f, lightingEffect: 1.0f, whiteClip: 0.5f), CreateSampleImage());
        var highClip = ApplyCpu(CreateSettings(luminance: 1.0f, toneCompression: 0.6f, lightingEffect: 1.0f, whiteClip: 3.0f), CreateSampleImage());

        Assert.That(MeanAbsoluteDifference(lowClip, highClip), Is.GreaterThan(0.05f));
    }

    [Test]
    public void ApplyInPlace_BlackClipChangesOutput()
    {
        var lowClip = ApplyCpu(CreateSettings(luminance: 1.0f, toneCompression: 0.6f, lightingEffect: 1.0f, blackClip: 0.0f), CreateSampleImage());
        var highClip = ApplyCpu(CreateSettings(luminance: 1.0f, toneCompression: 0.6f, lightingEffect: 1.0f, blackClip: 0.7f), CreateSampleImage());

        Assert.That(MeanAbsoluteDifference(lowClip, highClip), Is.GreaterThan(0.05f));
    }

    [Test]
    public void ApplyInPlaceGpu_LuminanceMatchesCpu()
    {
        using var context = CreateGpuContextOrSkip();
        var source = CreateSampleImage();
        var settings = CreateSettings(luminance: 2.0f, toneCompression: 0.6f, lightingEffect: 1.0f);

        var cpu = ApplyCpu(settings, Clone(source));
        var gpu = ApplyGpu(context, settings, Clone(source));

        AssertImagesClose(cpu, gpu, 2e-3f);
    }

    [Test]
    public void ApplyInPlaceGpu_LuminanceChangesOutputFromNeutralPreset()
    {
        using var context = CreateGpuContextOrSkip();
        var source = CreateSampleImage();
        var settings = new ContrastBalancerToneMapperSettings().MakeNeutral();
        settings.Luminance = 2.0f;

        var result = ApplyGpu(context, settings, Clone(source));

        Assert.That(MeanAbsoluteDifference(source, result), Is.GreaterThan(0.05f));
    }

    [Test]
    public void ApplyInPlaceGpu_ToneCompressionMatchesCpu()
    {
        using var context = CreateGpuContextOrSkip();
        var source = CreateSampleImage();
        var settings = CreateSettings(luminance: 1.0f, toneCompression: 0.2f, lightingEffect: 1.0f);

        var cpu = ApplyCpu(settings, Clone(source));
        var gpu = ApplyGpu(context, settings, Clone(source));

        AssertImagesClose(cpu, gpu, 2e-3f);
    }

    [Test]
    public void ApplyInPlaceGpu_ToneCompressionChangesOutputFromNeutralPreset()
    {
        using var context = CreateGpuContextOrSkip();
        var source = CreateSampleImage();
        var settings = new ContrastBalancerToneMapperSettings().MakeNeutral();
        settings.ToneCompression = 0.2f;

        var result = ApplyGpu(context, settings, Clone(source));

        Assert.That(MeanAbsoluteDifference(source, result), Is.GreaterThan(0.05f));
    }

    [Test]
    public void ApplyInPlaceGpu_LightingEffectMatchesCpu()
    {
        using var context = CreateGpuContextOrSkip();
        var source = CreateSampleImage();
        var settings = CreateSettings(luminance: 1.0f, toneCompression: 0.6f, lightingEffect: 2.0f);

        var cpu = ApplyCpu(settings, Clone(source));
        var gpu = ApplyGpu(context, settings, Clone(source));

        AssertImagesClose(cpu, gpu, 2e-3f);
    }

    [Test]
    public void ApplyInPlaceGpu_LightingEffectChangesOutputFromNeutralPreset()
    {
        using var context = CreateGpuContextOrSkip();
        var source = CreateSampleImage();
        var settings = new ContrastBalancerToneMapperSettings().MakeNeutral();
        settings.LightingEffect = 0.0f;

        var result = ApplyGpu(context, settings, Clone(source));

        Assert.That(MeanAbsoluteDifference(source, result), Is.GreaterThan(0.05f));
    }

    [Test]
    public void ApplyInPlaceGpu_WhiteClipMatchesCpu()
    {
        using var context = CreateGpuContextOrSkip();
        var source = CreateSampleImage();
        var settings = CreateSettings(luminance: 1.0f, toneCompression: 0.6f, lightingEffect: 1.0f, whiteClip: 0.5f);

        var cpu = ApplyCpu(settings, Clone(source));
        var gpu = ApplyGpu(context, settings, Clone(source));

        AssertImagesClose(cpu, gpu, 2e-3f);
    }

    [Test]
    public void ApplyInPlaceGpu_BlackClipMatchesCpu()
    {
        using var context = CreateGpuContextOrSkip();
        var source = CreateSampleImage();
        var settings = CreateSettings(luminance: 1.0f, toneCompression: 0.6f, lightingEffect: 1.0f, blackClip: 0.7f);

        var cpu = ApplyCpu(settings, Clone(source));
        var gpu = ApplyGpu(context, settings, Clone(source));

        AssertImagesClose(cpu, gpu, 2e-3f);
    }

    private static ContrastBalancerToneMapperSettings CreateSettings(
        float luminance,
        float toneCompression,
        float lightingEffect,
        float whiteClip = 1.0f,
        float blackClip = 0.0f)
    {
        var settings = new ContrastBalancerToneMapperSettings().MakeNeutral();
        settings.Strength = 1f;
        settings.Luminance = luminance;
        settings.ToneCompression = toneCompression;
        settings.LightingEffect = lightingEffect;
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

    private static HdrImage ApplyCpu(ContrastBalancerToneMapperSettings settings, HdrImage image)
    {
        ToneMapperFactory.Create(settings).ApplyInPlace(image);
        return image;
    }

    private static HdrImage ApplyGpu(GpuContext context, ContrastBalancerToneMapperSettings settings, HdrImage image)
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

    private static float MeanLuminance(HdrImage image)
    {
        return image.Pixels.Average(pixel => pixel.Light());
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
