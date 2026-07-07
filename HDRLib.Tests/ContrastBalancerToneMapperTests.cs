// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Tests;

using HDRLib.Gpu;
using HDRLib.Hdr.Debevec;
using HDRLib.Image;
using HDRLib.Interfaces;
using HDRLib.PixelProvider.ImageSharp;
using HDRLib.ToneMapping;
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
    public void ApplyInPlace_StrengthZeroIgnoresCoreControls()
    {
        var source = CreateSampleImage();
        var settings = new ContrastBalancerToneMapperSettings().MakeNeutral();
        settings.Strength = 0f;
        settings.Luminance = 2.0f;
        settings.ToneCompression = 0.2f;
        settings.LightingEffect = 0.0f;
        settings.WhiteClip = 0.5f;
        settings.BlackClip = 0.2f;

        var result = ApplyCpu(settings, Clone(source));

        AssertImagesClose(source, result, 1e-6f);
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
    public void ApplyInPlace_BaseControlsFromNeutralPresetChangeOutput()
    {
        AssertControlChangesOutput(settings => settings.ExposureEV = 1f, nameof(ContrastBalancerToneMapperSettings.ExposureEV));
        AssertControlChangesOutput(settings => settings.Brightness = 0.6f, nameof(ContrastBalancerToneMapperSettings.Brightness));
        AssertControlChangesOutput(settings => settings.Contrast = 1.6f, nameof(ContrastBalancerToneMapperSettings.Contrast));
        AssertControlChangesOutput(settings => settings.Saturation = 60f, nameof(ContrastBalancerToneMapperSettings.Saturation));
        AssertControlChangesOutput(settings => settings.Gamma = 2f, nameof(ContrastBalancerToneMapperSettings.Gamma));
        AssertControlChangesOutput(settings => settings.ShadowsBoost = 1.5f, nameof(ContrastBalancerToneMapperSettings.ShadowsBoost));
        AssertControlChangesOutput(settings => settings.MidtonesBoost = 1.5f, nameof(ContrastBalancerToneMapperSettings.MidtonesBoost));
        AssertControlChangesOutput(settings => settings.HighlightsBoost = 1.5f, nameof(ContrastBalancerToneMapperSettings.HighlightsBoost));
        AssertControlChangesOutput(settings => settings.Dehaze = 70f, nameof(ContrastBalancerToneMapperSettings.Dehaze));
        AssertControlChangesOutput(
            settings =>
            {
                settings.LocalContrast = 80f;
                settings.LocalContrastRadius = 1;
            },
            nameof(ContrastBalancerToneMapperSettings.LocalContrast));
        AssertControlChangesOutput(settings => settings.ColorTemperature = 4000f, nameof(ContrastBalancerToneMapperSettings.ColorTemperature));
        AssertControlChangesOutput(
            settings =>
            {
                settings.WhiteBalanceReferenceType = WhiteBalanceReferenceType.Gray;
                settings.WhiteBalanceReferenceColor = new Rgb(0.2f, 0.8f, 0.8f);
            },
            nameof(ContrastBalancerToneMapperSettings.WhiteBalanceReferenceType));
    }

    [Test]
    public void ApplyInPlace_AddingSaturationFiltersToImageAffectsExpectedPixelsAcrossFiveIterations()
    {
        var hues = new[] { 20f, 85f, 150f, 220f, 305f };
        var sourcePixels = hues.Select(hue => HsvToRgb(hue, 0.6f, 0.65f)).ToArray();
        var baseline = ApplyCpu(CreateImageFilterSettings([]), CreateImage(sourcePixels));
        var filters = new List<SaturationColorFilter>();

        for (var iteration = 0; iteration < 5; iteration++)
        {
            filters.Add(CreateDesaturatingFilterForHue(hues[iteration]));

            var filtered = ApplyCpu(CreateImageFilterSettings(filters), CreateImage(sourcePixels));

            for (var pixel = 0; pixel < sourcePixels.Length; pixel++)
            {
                var filteredChroma = Chroma(filtered.Pixels[pixel]);
                var baselineChroma = Chroma(baseline.Pixels[pixel]);

                if (pixel <= iteration)
                {
                    Assert.That(
                        filteredChroma,
                        Is.LessThan(baselineChroma * 0.25f),
                        $"Iteration {iteration + 1} should apply filter to pixel {pixel}.");
                }
                else
                {
                    Assert.That(
                        filteredChroma,
                        Is.EqualTo(baselineChroma).Within(1e-5f),
                        $"Iteration {iteration + 1} should not affect pixel {pixel} before its filter is added.");
                }
            }
        }
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
    public void ApplyInPlaceGpu_StrengthZeroIgnoresCoreControls()
    {
        using var context = CreateGpuContextOrSkip();
        var source = CreateSampleImage();
        var settings = new ContrastBalancerToneMapperSettings().MakeNeutral();
        settings.Strength = 0f;
        settings.Luminance = 2.0f;
        settings.ToneCompression = 0.2f;
        settings.LightingEffect = 0.0f;
        settings.WhiteClip = 0.5f;
        settings.BlackClip = 0.2f;

        var result = ApplyGpu(context, settings, Clone(source));

        AssertImagesClose(source, result, 1e-6f);
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

    [Test]
    public void ApplyInPlaceGpu_Accelerator1BaseControlsMatchCpu()
    {
        using var context = CreateGpuContextOrSkip(1);
        var cases = new Action<ContrastBalancerToneMapperSettings>[]
        {
            settings => settings.ExposureEV = 1f,
            settings => settings.Brightness = 0.6f,
            settings => settings.Contrast = 1.6f,
            settings => settings.Saturation = 60f,
            settings => settings.Gamma = 2f,
            settings => settings.Dehaze = 70f,
            settings =>
            {
                settings.LocalContrast = 80f;
                settings.LocalContrastRadius = 1;
            },
            settings => settings.ColorTemperature = 4000f,
            settings =>
            {
                settings.WhiteBalanceReferenceType = WhiteBalanceReferenceType.Gray;
                settings.WhiteBalanceReferenceColor = new Rgb(0.2f, 0.8f, 0.8f);
            }
        };

        for (var i = 0; i < cases.Length; i++)
        {
            var settings = new ContrastBalancerToneMapperSettings().MakeNeutral();
            cases[i](settings);
            var cpu = ApplyCpu(settings, Clone(CreateControlImage()));
            var gpu = ApplyGpu(context, settings, Clone(CreateControlImage()));

            AssertImagesClose(cpu, gpu, 2e-2f);
        }
    }

    [Test]
    public void ApplyInPlaceGpu_Accelerator1SaturationFiltersMatchCpu()
    {
        using var context = CreateGpuContextOrSkip(1);
        var hues = new[] { 20f, 85f, 150f, 220f, 305f };
        var sourcePixels = hues.Select(hue => HsvToRgb(hue, 0.6f, 0.65f)).ToArray();
        var settings = CreateImageFilterSettings(hues.Select(CreateDesaturatingFilterForHue));

        var cpu = ApplyCpu(settings, CreateImage(sourcePixels));
        var gpu = ApplyGpu(context, settings, CreateImage(sourcePixels));

        AssertImagesClose(cpu, gpu, 2e-2f);
    }

    [Test]
    public void ApplyInPlaceGpu_Accelerator1SingleImagePipelineMatchesCpu()
    {
        using var context = CreateGpuContextOrSkip(1);
        var settings = new ContrastBalancerToneMapperSettings
        {
            Strength = 0.85f,
            ToneCompression = 0.75f,
            LightingEffect = 1.15f,
            Luminance = 1.25f,
            WhiteClip = 1.35f,
            BlackClip = 0.05f,
            ExposureEV = 0.35f,
            Brightness = 0.9f,
            Contrast = 1.15f,
            Saturation = 25f,
            Gamma = 1.1f,
            Dehaze = 10f,
            LocalContrast = 20f,
            LocalContrastRadius = 3,
            ColorTemperature = 5200f
        };

        var previousAvxState = SystemHelper.UseAvxState;
        try
        {
            SystemHelper.UseAvxState = UseAvxState.Disable;
            var cpu = ProcessSingleImage(null, settings);
            var gpu = ProcessSingleImage(context, settings);
            var comparison = Compare(cpu, gpu);

            TestContext.Out.WriteLine($"Accelerator1 single-image pipeline: mean={comparison.Mean:F3}, max={comparison.Max}, p99={comparison.P99}");
            Assert.Multiple(() =>
            {
                Assert.That(comparison.Mean, Is.LessThanOrEqualTo(5.0));
                Assert.That(comparison.P99, Is.LessThanOrEqualTo(18));
                Assert.That(comparison.Max, Is.LessThanOrEqualTo(64));
            });
        }
        finally
        {
            SystemHelper.UseAvxState = previousAvxState;
        }
    }

    [Test]
    public void ApplyInPlaceGpu_Accelerator1SingleImagePipelineMatchesPreferredGpu()
    {
        using var preferred = CreateGpuContextOrSkip();
        using var accelerator1 = CreateGpuContextOrSkip(1);
        var settings = new ContrastBalancerToneMapperSettings
        {
            Strength = 0.85f,
            ToneCompression = 0.75f,
            LightingEffect = 1.15f,
            Luminance = 1.25f,
            WhiteClip = 1.35f,
            BlackClip = 0.05f,
            ExposureEV = 0.35f,
            Brightness = 0.9f,
            Contrast = 1.15f,
            Saturation = 25f,
            Gamma = 1.1f,
            Dehaze = 10f,
            LocalContrast = 20f,
            LocalContrastRadius = 3,
            ColorTemperature = 5200f
        };

        var expected = ProcessSingleImage(preferred, settings);
        var actual = ProcessSingleImage(accelerator1, settings);
        var comparison = Compare(expected, actual);

        TestContext.Out.WriteLine($"Preferred GPU -> accelerator1 pipeline: mean={comparison.Mean:F3}, max={comparison.Max}, p99={comparison.P99}");
        Assert.Multiple(() =>
        {
            Assert.That(comparison.Mean, Is.LessThanOrEqualTo(1.5));
            Assert.That(comparison.P99, Is.LessThanOrEqualTo(6));
            Assert.That(comparison.Max, Is.LessThanOrEqualTo(24));
        });
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

    private static HdrImage CreateControlImage()
    {
        return new HdrImage(3, 3)
        {
            Width = 3,
            Height = 3,
            Pixels =
            [
                new Rgb(0.08f, 0.06f, 0.05f),
                new Rgb(0.30f, 0.22f, 0.16f),
                new Rgb(0.70f, 0.60f, 0.45f),
                new Rgb(0.18f, 0.30f, 0.22f),
                new Rgb(0.50f, 0.42f, 0.36f),
                new Rgb(0.85f, 0.70f, 0.58f),
                new Rgb(0.05f, 0.08f, 0.14f),
                new Rgb(0.24f, 0.36f, 0.55f),
                new Rgb(0.62f, 0.48f, 0.78f)
            ]
        };
    }

    private static HdrImage CreateImage(Rgb[] pixels)
    {
        return new HdrImage(pixels.Length, 1)
        {
            Width = pixels.Length,
            Height = 1,
            Pixels = (Rgb[])pixels.Clone()
        };
    }

    private static ContrastBalancerToneMapperSettings CreateImageFilterSettings(IEnumerable<SaturationColorFilter> filters)
    {
        var settings = new ContrastBalancerToneMapperSettings().MakeNeutral();
        settings.Gamma = 1f;
        settings.SaturationFilters = filters.ToArray();
        return settings;
    }

    private static SaturationColorFilter CreateDesaturatingFilterForHue(float hue)
    {
        return new SaturationColorFilter
        {
            Enabled = true,
            SaturationAdjustment = -100f,
            Ranges =
            [
                new SaturationColorRange
                {
                    HueMin = hue - 20f,
                    HueMax = hue + 20f,
                    SaturationMin = 0.5f,
                    SaturationMax = 0.7f,
                    ValueMin = 0.6f,
                    ValueMax = 0.7f,
                    SaturationMultiplier = -100f
                }
            ]
        };
    }

    private static float Chroma(Rgb rgb)
    {
        return MathF.Max(rgb.Red, MathF.Max(rgb.Green, rgb.Blue)) -
               MathF.Min(rgb.Red, MathF.Min(rgb.Green, rgb.Blue));
    }

    private static Rgb HsvToRgb(float hue, float saturation, float value)
    {
        var chroma = value * saturation;
        var huePrime = hue / 60f;
        var x = chroma * (1f - MathF.Abs((huePrime % 2f) - 1f));
        var m = value - chroma;
        var (r, g, b) = huePrime switch
        {
            >= 0f and < 1f => (chroma, x, 0f),
            >= 1f and < 2f => (x, chroma, 0f),
            >= 2f and < 3f => (0f, chroma, x),
            >= 3f and < 4f => (0f, x, chroma),
            >= 4f and < 5f => (x, 0f, chroma),
            _ => (chroma, 0f, x)
        };

        return new Rgb(r + m, g + m, b + m);
    }

    private static HdrImage ApplyCpu(ContrastBalancerToneMapperSettings settings, HdrImage image)
    {
        ToneMapperFactory.Create(settings).ApplyInPlace(image);
        return image;
    }

    private static void AssertControlChangesOutput(Action<ContrastBalancerToneMapperSettings> configure, string controlName)
    {
        var source = CreateControlImage();
        var baseline = ApplyCpu(new ContrastBalancerToneMapperSettings().MakeNeutral(), Clone(source));
        var settings = new ContrastBalancerToneMapperSettings().MakeNeutral();
        configure(settings);

        var result = ApplyCpu(settings, Clone(source));

        Assert.That(
            MeanAbsoluteDifference(baseline, result),
            Is.GreaterThan(0.002f),
            $"{controlName} should change ContrastBalancer output from neutral preset.");
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

    private static byte[] ProcessSingleImage(GpuContext? context, ContrastBalancerToneMapperSettings settings)
    {
        using var source = LoadSampleImage();
        using var processor = context is null
            ? new SingleImageProcessor(source)
            : new SingleImageProcessor(context);

        if (context is not null)
        {
            processor.LoadSource(source);
        }

        processor.Process((ContrastBalancerToneMapperSettings)settings.Clone());
        using var result = processor.ToImage<ImageSharpProxy>();
        return LoadFullImage(result);
    }

    private static ImageSharpProxy LoadSampleImage()
    {
        var image = new ImageSharpProxy();
        image.Load(Path.Combine(TestContext.CurrentContext.TestDirectory, "Samples", "DSC_5299.JPG"));
        image.ImageProcessor.Resize(160, 106);
        return image;
    }

    private static byte[] LoadFullImage(IImageProxy image)
    {
        var bytes = new byte[image.Width * image.Height * 3];
        image.LoadFullImage(bytes);
        return bytes;
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

    private static ImageComparison Compare(byte[] expected, byte[] actual)
    {
        Assert.That(actual, Has.Length.EqualTo(expected.Length));
        var diffs = new int[expected.Length];
        long total = 0;
        var max = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            var diff = Math.Abs(expected[i] - actual[i]);
            diffs[i] = diff;
            total += diff;
            max = Math.Max(max, diff);
        }

        Array.Sort(diffs);
        var p99Index = Math.Clamp((int)Math.Ceiling(diffs.Length * 0.99) - 1, 0, diffs.Length - 1);
        return new ImageComparison((double)total / expected.Length, max, diffs[p99Index]);
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

    private static GpuContext CreateGpuContextOrSkip(int acceleratorNumber = -1)
    {
        try
        {
            if (acceleratorNumber >= GpuContext.GetAccelerators().Count)
            {
                Assert.Ignore($"GPU accelerator {acceleratorNumber} is not available.");
            }

            return new GpuContext(acceleratorNumber);
        }
        catch (Exception ex)
        {
            Assert.Ignore($"GPU accelerator {acceleratorNumber} is not available: {ex.Message}");
            throw;
        }
    }

    private readonly record struct ImageComparison(double Mean, int Max, int P99);
}
