// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Tests;

using HDRLib.Image;
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
    public void Analyze_ImageWithLargeHighlightMass_ComputesHighlightCompressionForPostProcessor()
    {
        var pixels = Enumerable.Repeat(new Rgb(0.95f, 0.95f, 0.95f), 40)
            .Concat(Enumerable.Repeat(new Rgb(0.35f, 0.35f, 0.35f), 60))
            .ToArray();

        var auto = ImageAnalyzer.Analyze(pixels);

        Assert.That(auto.HighlightCompression, Is.GreaterThan(1f));
        Assert.That(auto.ToPostProcessSettings().Highlights, Is.EqualTo(auto.HighlightCompression));
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
            Assert.That(settings.Exposure, Is.EqualTo(auto.ExposureEV).Within(1e-6f));
            Assert.That(settings.Brightness, Is.EqualTo(auto.Brightness * 1.10f).Within(1e-6f));
            Assert.That(settings.Shadows, Is.EqualTo(auto.Shadows).Within(1e-6f));
            Assert.That(settings.Midtones, Is.EqualTo(auto.Midtones).Within(1e-6f));
            Assert.That(settings.Highlights, Is.EqualTo(auto.HighlightCompression).Within(1e-6f));
            Assert.That(settings.Contrast, Is.EqualTo(auto.Contrast).Within(1e-6f));
            Assert.That(settings.Vibrance, Is.EqualTo(auto.Saturation).Within(1e-6f));
        });
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
}
