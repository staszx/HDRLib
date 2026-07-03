// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Tests;

using HDRLib.Image;
using HDRLib.Post;
using HDRLib.PostProcessors;
using HDRLib.ToneMapping;
using HDRLib.ToneMapping.Settings;
using NUnit.Framework;

public class ImageAnalyzerTests
{
    [Test]
    public void Analyze_EmptyImage_ReturnsNeutralPostProcessSettings()
    {
        var settings = ImageAnalyzer.Analyze(Array.Empty<Rgb>()).ToPostProcessSettings();

        Assert.That(settings.Exposure, Is.EqualTo(0f));
        Assert.That(settings.Brightness, Is.EqualTo(1f));
        Assert.That(settings.Shadows, Is.EqualTo(1f));
        Assert.That(settings.Midtones, Is.EqualTo(1f));
        Assert.That(settings.Highlights, Is.EqualTo(1f));
        Assert.That(settings.Contrast, Is.EqualTo(1f));
        Assert.That(settings.Vibrance, Is.EqualTo(1f));
    }

    [Test]
    public void Analyze_DarkImage_ComputesShadowLift()
    {
        var pixels = Enumerable.Repeat(new Rgb(0.08f, 0.08f, 0.08f), 100).ToArray();

        var auto = ImageAnalyzer.Analyze(pixels);

        Assert.That(auto.Shadows, Is.GreaterThan(1f));
        Assert.That(auto.ToPostProcessSettings().Shadows, Is.EqualTo(auto.Shadows));
    }

    [Test]
    public void Analyze_ImageWithLargeHighlightMass_ComputesHighlightCompressionButDoesNotAutoApplyIt()
    {
        var pixels = Enumerable.Repeat(new Rgb(0.95f, 0.95f, 0.95f), 40)
            .Concat(Enumerable.Repeat(new Rgb(0.35f, 0.35f, 0.35f), 60))
            .ToArray();

        var auto = ImageAnalyzer.Analyze(pixels);

        Assert.That(auto.HighlightCompression, Is.GreaterThan(1f));
        Assert.That(auto.ToPostProcessSettings().Highlights, Is.EqualTo(1f));
    }

    [Test]
    public void ToPostProcessSettings_DoesNotApplyUnsafeAutoDarkeningControls()
    {
        var auto = new ImageAdjustSettings
        {
            ExposureEV = -1.5f,
            Brightness = 0.75f,
            Shadows = 1.2f,
            Midtones = 0.9f,
            HighlightCompression = 1.4f,
            Contrast = 1.2f,
            Saturation = 1.1f
        };

        var settings = auto.ToPostProcessSettings();

        Assert.Multiple(() =>
        {
            Assert.That(settings.Exposure, Is.EqualTo(0f));
            Assert.That(settings.Brightness, Is.EqualTo(1f));
            Assert.That(settings.Shadows, Is.EqualTo(auto.Shadows));
            Assert.That(settings.Midtones, Is.EqualTo(1f));
            Assert.That(settings.Highlights, Is.EqualTo(1f));
            Assert.That(settings.Contrast, Is.EqualTo(auto.Contrast));
            Assert.That(settings.Vibrance, Is.EqualTo(auto.Saturation));
        });
    }

    [Test]
    public void PostProcessSettings_Combine_ComposesExposureAdditivelyAndOtherControlsMultiplicatively()
    {
        var first = new PostProcessSettings { Exposure = 0.5f, Brightness = 1.1f, Shadows = 1.2f, Contrast = 1.3f };
        var second = new PostProcessSettings { Exposure = -0.25f, Brightness = 1.2f, Shadows = 1.1f, Contrast = 1.4f };

        var combined = first.Combine(second);

        Assert.Multiple(() =>
        {
            Assert.That(combined.Exposure, Is.EqualTo(0.25f).Within(1e-6f));
            Assert.That(combined.Brightness, Is.EqualTo(1.32f).Within(1e-6f));
            Assert.That(combined.Shadows, Is.EqualTo(1.32f).Within(1e-6f));
            Assert.That(combined.Contrast, Is.EqualTo(1.82f).Within(1e-6f));
        });
    }

    [Test]
    public void WithAutoAdjust_IncludesToneRegionSettings()
    {
        var auto = new ImageAdjustSettings
        {
            ExposureEV = 0.5f,
            Brightness = 1.1f,
            Shadows = 1.2f,
            Midtones = 1.3f,
            HighlightCompression = 1.4f,
            Contrast = 1.5f,
            Saturation = 1.6f
        };

        var settings = new PostProcessSettings().WithAutoAdjust(auto);

        Assert.Multiple(() =>
        {
            Assert.That(settings.Exposure, Is.EqualTo(MathF.Max(0f, auto.ExposureEV)).Within(1e-6f));
            Assert.That(settings.Brightness, Is.EqualTo(auto.Brightness).Within(1e-6f));
            Assert.That(settings.Shadows, Is.EqualTo(auto.Shadows).Within(1e-6f));
            Assert.That(settings.Midtones, Is.EqualTo(auto.Midtones).Within(1e-6f));
            Assert.That(settings.Highlights, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(settings.Contrast, Is.EqualTo(auto.Contrast).Within(1e-6f));
            Assert.That(settings.Vibrance, Is.EqualTo(auto.Saturation).Within(1e-6f));
        });
    }

    [Test]
    public void Analyze_AutoPostProcess_DoesNotExcessivelyDarkenBalancedLdrImage()
    {
        var image = CreateBalancedLdrPixels();
        var baseline = MeanLuminance(image);
        var settings = ImageAnalyzer.Analyze(image).ToPostProcessSettings();
        var adjusted = new HDRLib.Image.Image<Rgb>(4, 4)
        {
            Width = 4,
            Height = 4,
            Pixels = (Rgb[])image.Clone()
        };

        new LabPostProcessor(settings).ApplyInPlace(adjusted);
        var adjustedMean = MeanLuminance(adjusted.Pixels);

        TestContext.Out.WriteLine($"Auto settings: {ImageAnalyzer.Analyze(image)}");
        TestContext.Out.WriteLine($"Mean luminance: baseline={baseline:F6}, adjusted={adjustedMean:F6}");

        Assert.That(adjustedMean, Is.GreaterThanOrEqualTo(baseline * 0.95f));
    }

    [Test]
    public void ToneMapperSettingsNeutrality_IncludesPostProcessSettings()
    {
        var settings = new AcesFilmicTonemapperSettings().MakeNeutral();
        settings.PostProcess.Brightness = 1.2f;

        Assert.That(settings.IsNeutral(), Is.False);

        settings.MakeNeutral();

        Assert.That(settings.PostProcess.IsNeutral(), Is.True);
    }

    [Test]
    public void AutoAdjustEnabled_SerializesCheckboxState()
    {
        var settings = new AcesFilmicTonemapperSettings();

        settings.AutoAdjustEnabled = true;

        Assert.Multiple(() =>
        {
            Assert.That(settings.AutoAdjustEnabled, Is.True);
            Assert.That(settings.ToXml(), Does.Contain($"<{nameof(ToneMapperSettings.AutoAdjustEnabled)}>true</{nameof(ToneMapperSettings.AutoAdjustEnabled)}>"));
        });

        settings.AutoAdjustEnabled = false;

        Assert.Multiple(() =>
        {
            Assert.That(settings.AutoAdjustEnabled, Is.False);
            Assert.That(settings.ToXml(), Does.Contain($"<{nameof(ToneMapperSettings.AutoAdjustEnabled)}>false</{nameof(ToneMapperSettings.AutoAdjustEnabled)}>"));
        });
    }

    private static Rgb[] CreateBalancedLdrPixels()
    {
        return
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
        ];
    }

    private static float MeanLuminance(Rgb[] pixels)
    {
        var sum = 0f;
        foreach (var pixel in pixels)
        {
            sum += pixel.Light();
        }

        return sum / pixels.Length;
    }
}
