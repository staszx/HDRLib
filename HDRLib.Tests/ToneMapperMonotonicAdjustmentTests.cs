// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Tests;

using Gpu;
using Hdr.Debevec;
using HDRLib.Image;
using HDRLib.ToneMapping.Factories;
using HDRLib.ToneMapping.Settings;
using ILGPU.Runtime;
using NUnit.Framework;

public class ToneMapperMonotonicAdjustmentTests
{
    private const float BrightnessTolerance = 1e-5f;
    private const float VibranceLuminanceTolerance = 0.03f;

    [TestCaseSource(nameof(AllToneMappers))]
    public void Brightness_IncreasesMeanLuminanceMonotonically(ToneMapperSettings settings)
    {
        var previous = float.NegativeInfinity;

        for (var slider = 0; slider <= 10; slider += 5)
        {
            var candidate = CloneSettings(settings);
            candidate.Brightness = SliderToPositiveMultiplier(slider);

            var mean = ProcessAndMeasureMeanLuminance(candidate);
            Assert.That(mean, Is.GreaterThanOrEqualTo(previous - BrightnessTolerance),
                $"{settings.GetType().Name} brightness slider {slider} produced {mean:F6} after {previous:F6}.");
            previous = mean;
        }
    }

    [Test, Ignore("local")]
    public void NaturalBrightness_WithSaturation_IncreasesMeanLuminanceMonotonically()
    {
        var previous = float.NegativeInfinity;

        for (var slider = 0; slider <= 50; slider += 5)
        {
            var settings = new NaturalToneMapperSettings().MakeNeutral();
            settings.Brightness = SliderToPositiveMultiplier(slider);
            settings.Saturation = 10f;

            var mean = ProcessAndMeasureMeanLuminance(settings);
            Assert.That(mean, Is.GreaterThanOrEqualTo(previous - BrightnessTolerance),
                $"Natural brightness slider {slider} with saturation produced {mean:F6} after {previous:F6}.");
            previous = mean;
        }
    }

    [Test]
    public void NaturalBrightness_WithSaturation_IncreasesMeanLuminanceMonotonicallyOnGpu()
    {
        using var context = CreateGpuContextOrSkip();
        var previous = float.NegativeInfinity;

        for (var slider = 0; slider <= 50; slider += 5)
        {
            var settings = new NaturalToneMapperSettings().MakeNeutral();
            settings.Brightness = SliderToPositiveMultiplier(slider);
            settings.Saturation = 10f;

            var mean = ProcessAndMeasureMeanLuminanceOnGpu(context, settings);
            Assert.That(mean, Is.GreaterThanOrEqualTo(previous - BrightnessTolerance),
                $"Natural GPU brightness slider {slider} with saturation produced {mean:F6} after {previous:F6}.");
            previous = mean;
        }
    }

    [TestCaseSource(nameof(AllToneMappers))]
    public void Exposure_IncreasesMeanLuminanceMonotonically(ToneMapperSettings settings)
    {
        var previous = float.NegativeInfinity;

        for (var slider = -100; slider <= 100; slider += 5)
        {
            var candidate = CloneSettings(settings);
            candidate.ExposureEV = slider / 10f;

            var mean = ProcessAndMeasureMeanLuminance(candidate);
            Assert.That(mean, Is.GreaterThanOrEqualTo(previous - BrightnessTolerance),
                $"{settings.GetType().Name} exposure slider {slider} produced {mean:F6} after {previous:F6}.");
            previous = mean;
        }
    }

    [TestCaseSource(nameof(AllToneMappers))]
    public void AutoAdjust_PreservesManualExposureAndBrightness(ToneMapperSettings settings)
    {
        var auto = CloneSettings(settings);
        auto.AutoAdjustEnabled = true;
        var autoMean = ProcessAndMeasureMeanLuminance(auto);

        var autoWithExposure = CloneSettings(settings);
        autoWithExposure.AutoAdjustEnabled = true;
        autoWithExposure.ExposureEV = 1f;
        var exposedMean = ProcessAndMeasureMeanLuminance(autoWithExposure);

        var autoWithBrightness = CloneSettings(settings);
        autoWithBrightness.AutoAdjustEnabled = true;
        autoWithBrightness.Brightness = 1.25f;
        var brightMean = ProcessAndMeasureMeanLuminance(autoWithBrightness);

        Assert.Multiple(() =>
        {
            Assert.That(autoMean, Is.GreaterThan(0.05f), $"{settings.GetType().Name} auto adjust darkened the image excessively.");
            Assert.That(exposedMean, Is.GreaterThanOrEqualTo(autoMean - BrightnessTolerance),
                $"{settings.GetType().Name} auto adjust plus exposure should not be darker than auto adjust.");
            Assert.That(brightMean, Is.GreaterThanOrEqualTo(autoMean - BrightnessTolerance),
                $"{settings.GetType().Name} auto adjust plus brightness should not be darker than auto adjust.");
        });
    }

    [TestCaseSource(nameof(AllToneMappers))]
    public void AutoAdjust_DoesNotExcessivelyDarkenComparedToOff(ToneMapperSettings settings)
    {
        var withoutAuto = CloneSettings(settings);
        var baselineMean = ProcessAndMeasureMeanLuminance(withoutAuto);

        var withAuto = CloneSettings(settings);
        withAuto.AutoAdjustEnabled = true;
        var autoMean = ProcessAndMeasureMeanLuminance(withAuto);

        Assert.That(autoMean, Is.GreaterThanOrEqualTo(baselineMean * 0.95f),
            $"{settings.GetType().Name} auto adjust changed mean luminance from {baselineMean:F6} to {autoMean:F6}.");
    }

    [Test]
    public void AcesFilmic_VibranceDoesNotMateriallyChangeMeanLuminance()
    {
        var baseline = ProcessAndMeasureMeanLuminance(new AcesFilmicTonemapperSettings());

        for (var vibrance = 0; vibrance <= 5; vibrance++)
        {
            var settings = new AcesFilmicTonemapperSettings
            {
                Saturation = vibrance * 20f
            };

            var mean = ProcessAndMeasureMeanLuminance(settings);
            Assert.That(mean, Is.EqualTo(baseline).Within(VibranceLuminanceTolerance),
                $"AcesFilm saturation/vibrance value {vibrance} changed mean luminance from {baseline:F6} to {mean:F6}.");
        }
    }

    [Test]
    public void AcesFilmic_ContrastDoesNotDarkenWhenIncreased()
    {
        var previous = float.NegativeInfinity;

        for (var slider = -100; slider <= 100; slider += 5)
        {
            var settings = new AcesFilmicTonemapperSettings
            {
                Contrast = SliderToPositiveMultiplier(slider)
            };

            var mean = ProcessAndMeasureMeanLuminance(settings);
            Assert.That(mean, Is.GreaterThanOrEqualTo(previous - BrightnessTolerance),
                $"AcesFilmic contrast slider {slider} produced {mean:F6} after {previous:F6}.");
            previous = mean;
        }
    }

    [TestCaseSource(nameof(AllToneMappers))]
    public void Saturation_WithContrast_IncreasesMeanChromaMonotonically(ToneMapperSettings settings)
    {
        var previous = float.NegativeInfinity;

        for (var slider = 0; slider <= 100; slider += 5)
        {
            var candidate = CloneSettings(settings);
            candidate.Contrast = SliderToPositiveMultiplier(25);
            candidate.Saturation = slider;

            var chroma = ProcessAndMeasureMeanChroma(candidate);
            Assert.That(chroma, Is.GreaterThanOrEqualTo(previous - BrightnessTolerance),
                $"{settings.GetType().Name} saturation slider {slider} with contrast produced chroma {chroma:F6} after {previous:F6}.");
            previous = chroma;
        }
    }

    private static IEnumerable<TestCaseData> AllToneMappers()
    {
        yield return CreateTestCase(new AcesFilmicTonemapperSettings());
        yield return CreateTestCase(new NaturalToneMapperSettings());
        yield return CreateTestCase(new ContrastBalancerToneMapperSettings());
        yield return CreateTestCase(new BrightnessBalancerToneMapperSettings());
    }

    private static TestCaseData CreateTestCase(ToneMapperSettings settings)
    {
        return new TestCaseData(settings).SetArgDisplayNames(settings.GetType().Name);
    }

    private static ToneMapperSettings CloneSettings(ToneMapperSettings settings)
    {
        return settings.Clone().MakeNeutral();
    }

    private static float ProcessAndMeasureMeanLuminance(ToneMapperSettings settings)
    {
        var image = CreateSmallImage();
        ToneMapperFactory.Create(settings).ApplyInPlace(image);
        
        return MeanLuminance(image);
    }

    private static float ProcessAndMeasureMeanChroma(ToneMapperSettings settings)
    {
        var image = CreateSmallImage();
        ToneMapperFactory.Create(settings).ApplyInPlace(image);

        return MeanChroma(image);
    }

    private static float ProcessAndMeasureMeanLuminanceOnGpu(GpuContext context, NaturalToneMapperSettings settings)
    {
        var image = CreateSmallImage();
        var mapper = ToneMapperFactoryGpu.Create(context, settings);
        using var pixels = context.Accelerator.Allocate1D<Rgb>(image.Pixels.Length);
        pixels.CopyFromCPU(image.Pixels);
        mapper.ApplyInPlace(pixels.View, image.Width, image.Height);
        image.Pixels = pixels.GetAsArray1D();

        return MeanLuminance(image);
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

    private static float SliderToPositiveMultiplier(int slider)
    {
        return slider <= 0
            ? 1f + (slider / 100f)
            : 1f + (slider / 50f);
    }

    private static Image<Rgb> CreateSmallImage()
    {
        return new Image<Rgb>(4, 4)
        {
            Width = 4,
            Height = 4,
            Pixels =
            [
                new Rgb(0.03f, 0.05f, 0.08f),
                new Rgb(0.08f, 0.10f, 0.12f),
                new Rgb(0.14f, 0.16f, 0.18f),
                new Rgb(0.20f, 0.22f, 0.24f),
                new Rgb(0.28f, 0.24f, 0.20f),
                new Rgb(0.32f, 0.35f, 0.30f),
                new Rgb(0.40f, 0.43f, 0.46f),
                new Rgb(0.48f, 0.52f, 0.56f),
                new Rgb(0.58f, 0.48f, 0.38f),
                new Rgb(0.62f, 0.66f, 0.70f),
                new Rgb(0.72f, 0.68f, 0.60f),
                new Rgb(0.78f, 0.80f, 0.76f),
                new Rgb(0.85f, 0.78f, 0.70f),
                new Rgb(0.90f, 0.92f, 0.94f),
                new Rgb(0.96f, 0.88f, 0.74f),
                new Rgb(1.00f, 0.96f, 0.90f)
            ]
        };
    }

    private static float MeanLuminance(Image<Rgb> image)
    {
        var sum = 0f;
        foreach (var pixel in image.Pixels)
        {
            sum += pixel.Light();
        }

        return sum / image.Pixels.Length;
    }

    private static float MeanChroma(Image<Rgb> image)
    {
        var sum = 0f;
        foreach (var pixel in image.Pixels)
        {
            var max = MathF.Max(pixel.Red, MathF.Max(pixel.Green, pixel.Blue));
            var min = MathF.Min(pixel.Red, MathF.Min(pixel.Green, pixel.Blue));
            sum += max - min;
        }

        return sum / image.Pixels.Length;
    }
}
