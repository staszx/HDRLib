// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Tests;

using System.Linq;
using HDRLib.Gpu;
using HDRLib.Image;
using HDRLib.ToneMapping;
using HDRLib.ToneMapping.Factories;
using HDRLib.ToneMapping.Settings;
using ILGPU.Runtime;
using NUnit.Framework;

public class NaturalToneMapperTests
{
    [Test]
    public void ApplyInPlace_PreservesNeutralGrayWithoutColorShift()
    {
        var mapper = ToneMapperFactory.Create(new NaturalToneMapperSettings());
        var image = new Image<Rgb>(1, 1)
        {
            Pixels = [new Rgb(2.0f, 2.0f, 2.0f)]
        };

        mapper.ApplyInPlace(image);

        Assert.Multiple(() =>
        {
            Assert.That(image.Pixels[0].Red, Is.EqualTo(image.Pixels[0].Green).Within(1e-5f));
            Assert.That(image.Pixels[0].Green, Is.EqualTo(image.Pixels[0].Blue).Within(1e-5f));
        });
    }

    [Test]
    public void ApplyInPlace_PreservesChannelRatioAsGammaInvariantBehavior()
    {
        var mapper = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            Contrast = 1f,
            Saturation = 0f,
            Brightness = 1f,
            Gamma = 1f
        });

        var image = new Image<Rgb>(1, 1)
        {
            Pixels = [new Rgb(0.4f, 0.2f, 0.1f)]
        };

        mapper.ApplyInPlace(image);
        var px = image.Pixels[0];

        Assert.Multiple(() =>
        {
            Assert.That(px.Red / px.Green, Is.EqualTo(2f).Within(1e-4f));
            Assert.That(px.Green / px.Blue, Is.EqualTo(2f).Within(1e-4f));
        });
    }

    [Test]
    public void ApplyInPlace_DefaultsToAutomaticBrightnessCompensation()
    {
        var basePixel = new Rgb(0.6f, 0.5f, 0.4f);
        var withCompensation = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            AutoBrightnessCompensation = true
        });
        var withoutCompensation = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            AutoBrightnessCompensation = false,
            Gamma = 1f
        });

        var imageWithCompensation = new Image<Rgb>(1, 1) { Pixels = [basePixel] };
        var imageWithoutCompensation = new Image<Rgb>(1, 1) { Pixels = [basePixel] };

        withCompensation.ApplyInPlace(imageWithCompensation);
        withoutCompensation.ApplyInPlace(imageWithoutCompensation);

        Assert.That(
            MathF.Abs(imageWithCompensation.Pixels[0].Light() - 0.33f),
            Is.LessThan(MathF.Abs(imageWithoutCompensation.Pixels[0].Light() - 0.33f)));
    }

    [Test]
    public void ApplyInPlace_AutoBrightnessCanDarkenTowardOutputMidGray()
    {
        var pixel = new Rgb(3.0f, 2.5f, 2.0f);
        var withCompensation = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            AutoBrightnessCompensation = true
        });
        var withoutCompensation = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            AutoBrightnessCompensation = false,
            Gamma = 1f
        });

        var autoImage = new Image<Rgb>(1, 1) { Pixels = [pixel] };
        var manualImage = new Image<Rgb>(1, 1) { Pixels = [pixel] };

        withCompensation.ApplyInPlace(autoImage);
        withoutCompensation.ApplyInPlace(manualImage);

        Assert.That(autoImage.Pixels[0].Light(), Is.LessThan(manualImage.Pixels[0].Light()));
    }

    [Test]
    public void ApplyInPlace_BypassesCompressionForLdrInputWhenEnabled()
    {
        var image = new Image<Rgb>(2, 1)
        {
            Pixels =
            [
                new Rgb(0.8f, 0.4f, 0.2f),
                new Rgb(0.4f, 0.2f, 0.1f)
            ]
        };

        var mapper = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            BypassToneCompressionForLdr = true,
            AutoBrightnessCompensation = false,
            Contrast = 1f,
            Saturation = 0f,
            Brightness = 1f,
            Gamma = 1f
        });

        mapper.ApplyInPlace(image);

        Assert.Multiple(() =>
        {
            Assert.That(image.Pixels[0].Red, Is.EqualTo(0.8f).Within(1e-5f));
            Assert.That(image.Pixels[0].Green, Is.EqualTo(0.4f).Within(1e-5f));
            Assert.That(image.Pixels[0].Blue, Is.EqualTo(0.2f).Within(1e-5f));
            Assert.That(image.Pixels[1].Red, Is.EqualTo(0.4f).Within(1e-5f));
            Assert.That(image.Pixels[1].Green, Is.EqualTo(0.2f).Within(1e-5f));
            Assert.That(image.Pixels[1].Blue, Is.EqualTo(0.1f).Within(1e-5f));
        });
    }

    [Test]
    public void ApplyInPlace_LdrBypassStillAppliesContrastAndSaturationControls()
    {
        var source = new Rgb(0.7f, 0.5f, 0.3f);
        var lowAdjustments = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            BypassToneCompressionForLdr = true,
            AutoBrightnessCompensation = false,
            Contrast = 1f,
            Saturation = 0f,
            Gamma = 1f
        });
        var highAdjustments = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            BypassToneCompressionForLdr = true,
            AutoBrightnessCompensation = false,
            Contrast = 3f,
            Saturation = 100f,
            Gamma = 1f
        });

        var lowImage = new Image<Rgb>(1, 1) { Pixels = [source] };
        var highImage = new Image<Rgb>(1, 1) { Pixels = [source] };

        lowAdjustments.ApplyInPlace(lowImage);
        highAdjustments.ApplyInPlace(highImage);

        var low = lowImage.Pixels[0];
        var high = highImage.Pixels[0];
        var lowChromaticSpread = MathF.Abs(low.Red - low.Blue);
        var highChromaticSpread = MathF.Abs(high.Red - high.Blue);

        Assert.Multiple(() =>
        {
            Assert.That(high.Light(), Is.Not.EqualTo(low.Light()).Within(1e-4f));
            Assert.That(highChromaticSpread, Is.GreaterThan(lowChromaticSpread));
        });
    }

    [Test]
    public void ApplyInPlace_OutputMidGrayControlsCompressedBrightnessInBothDirections()
    {
        var lowOutput = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            BypassToneCompressionForLdr = false,
            AutoBrightnessCompensation = true,
            OutputMidGray = 0.12f,
            ShadowsBoost = 1f,
            MidtonesBoost = 1f,
            HighlightsBoost = 1f,
            Contrast = 1f,
            Saturation = 0f,
            Gamma = 1f
        });
        var highOutput = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            BypassToneCompressionForLdr = false,
            AutoBrightnessCompensation = true,
            OutputMidGray = 0.60f,
            ShadowsBoost = 1f,
            MidtonesBoost = 1f,
            HighlightsBoost = 1f,
            Contrast = 1f,
            Saturation = 0f,
            Gamma = 1f
        });

        var lowImage = new Image<Rgb>(3, 1) { Pixels = [new Rgb(0.25f, 0.25f, 0.25f), new Rgb(1.0f, 1.0f, 1.0f), new Rgb(4.0f, 4.0f, 4.0f)] };
        var highImage = new Image<Rgb>(3, 1) { Pixels = [new Rgb(0.25f, 0.25f, 0.25f), new Rgb(1.0f, 1.0f, 1.0f), new Rgb(4.0f, 4.0f, 4.0f)] };

        lowOutput.ApplyInPlace(lowImage);
        highOutput.ApplyInPlace(highImage);

        Assert.That(highImage.Pixels.Average(p => p.Light()), Is.GreaterThan(lowImage.Pixels.Average(p => p.Light()) + 0.1f));
    }

    [Test]
    public void ApplyInPlace_OutputMidGrayAffectsLdrBypassWhenAutoCompensationEnabled()
    {
        var lowOutput = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            BypassToneCompressionForLdr = true,
            AutoBrightnessCompensation = true,
            OutputMidGray = 0.20f,
            Contrast = 1f,
            Saturation = 0f,
            Gamma = 1f
        });
        var highOutput = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            BypassToneCompressionForLdr = true,
            AutoBrightnessCompensation = true,
            OutputMidGray = 0.60f,
            Contrast = 1f,
            Saturation = 0f,
            Gamma = 1f
        });

        var lowImage = new Image<Rgb>(2, 1) { Pixels = [new Rgb(0.35f, 0.35f, 0.35f), new Rgb(0.65f, 0.65f, 0.65f)] };
        var highImage = new Image<Rgb>(2, 1) { Pixels = [new Rgb(0.35f, 0.35f, 0.35f), new Rgb(0.65f, 0.65f, 0.65f)] };

        lowOutput.ApplyInPlace(lowImage);
        highOutput.ApplyInPlace(highImage);

        Assert.That(highImage.Pixels.Average(p => p.Light()), Is.GreaterThan(lowImage.Pixels.Average(p => p.Light()) + 0.1f));
    }

    [Test]
    public void ApplyInPlace_DefaultGammaBrightensMidtonesComparedToGammaOne()
    {
        var source = new Rgb(0.45f, 0.35f, 0.25f);
        var gammaOneMapper = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            Gamma = 1f
        });
        var defaultGammaMapper = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            Gamma = 2.2f
        });

        var gammaOneImage = new Image<Rgb>(1, 1) { Pixels = [source] };
        var defaultGammaImage = new Image<Rgb>(1, 1) { Pixels = [source] };

        gammaOneMapper.ApplyInPlace(gammaOneImage);
        defaultGammaMapper.ApplyInPlace(defaultGammaImage);

        Assert.That(defaultGammaImage.Pixels[0].Light(), Is.GreaterThan(gammaOneImage.Pixels[0].Light()));
    }

    [Test]
    public void ApplyInPlace_ExposureEvControlsOverallExposure()
    {
        var source = new Rgb(0.8f, 0.6f, 0.4f);
        var darkerMapper = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            ExposureEV = -1f,
            BypassToneCompressionForLdr = false,
            AutoBrightnessCompensation = false,
            Contrast = 1f,
            Saturation = 0f,
            Gamma = 1f
        });
        var brighterMapper = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            ExposureEV = 1f,
            BypassToneCompressionForLdr = false,
            AutoBrightnessCompensation = false,
            Contrast = 1f,
            Saturation = 0f,
            Gamma = 1f
        });

        var darkerImage = new Image<Rgb>(1, 1) { Pixels = [source] };
        var brighterImage = new Image<Rgb>(1, 1) { Pixels = [source] };

        darkerMapper.ApplyInPlace(darkerImage);
        brighterMapper.ApplyInPlace(brighterImage);

        Assert.That(brighterImage.Pixels[0].Light(), Is.GreaterThan(darkerImage.Pixels[0].Light()));
    }

    [Test]
    public void ApplyInPlace_LdrBypassBrightnessZeroIsDarkerThanPositiveBrightnessWhenContrastIsFlat()
    {
        var source = new Rgb(0.6f, 0.5f, 0.4f);
        var darkMapper = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            BypassToneCompressionForLdr = true,
            AutoBrightnessCompensation = true,
            Brightness = 0f,
            Contrast = 0f,
            Gamma = 1f
        });
        var brighterMapper = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            BypassToneCompressionForLdr = true,
            AutoBrightnessCompensation = true,
            Brightness = 0.3f,
            Contrast = 0f,
            Gamma = 1f
        });

        var darkImage = new Image<Rgb>(1, 1) { Pixels = [source] };
        var brighterImage = new Image<Rgb>(1, 1) { Pixels = [source] };

        darkMapper.ApplyInPlace(darkImage);
        brighterMapper.ApplyInPlace(brighterImage);

        Assert.That(darkImage.Pixels[0].Light(), Is.LessThan(brighterImage.Pixels[0].Light()));
    }

    [Test]
    public void ApplyInPlace_CompressedBrightnessZeroIsDarkerThanPositiveBrightnessWhenContrastIsFlat()
    {
        var source = new Rgb(2.0f, 1.6f, 1.2f);
        var darkMapper = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            BypassToneCompressionForLdr = false,
            AutoBrightnessCompensation = true,
            Brightness = 0f,
            Contrast = 0f,
            Gamma = 1f
        });
        var brighterMapper = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            BypassToneCompressionForLdr = false,
            AutoBrightnessCompensation = true,
            Brightness = 0.3f,
            Contrast = 0f,
            Gamma = 1f
        });

        var darkImage = new Image<Rgb>(1, 1) { Pixels = [source] };
        var brighterImage = new Image<Rgb>(1, 1) { Pixels = [source] };

        darkMapper.ApplyInPlace(darkImage);
        brighterMapper.ApplyInPlace(brighterImage);

        Assert.That(darkImage.Pixels[0].Light(), Is.LessThan(brighterImage.Pixels[0].Light()));
    }

    [Test]
    public void ApplyInPlace_DehazeReducesNeutralVeilAndPreservesColorSeparation()
    {
        var settings = new NaturalToneMapperSettings().MakeNeutral();
        settings.Dehaze = 70f;
        var mapper = ToneMapperFactory.Create(settings);
        var image = new Image<Rgb>(3, 1)
        {
            Pixels =
            [
                new Rgb(0.05f, 0.05f, 0.05f),
                new Rgb(0.55f, 0.55f, 0.55f),
                new Rgb(0.70f, 0.58f, 0.50f)
            ]
        };

        mapper.ApplyInPlace(image);

        var neutralVeil = image.Pixels[1];
        var colored = image.Pixels[2];

        Assert.Multiple(() =>
        {
            Assert.That(image.Pixels[0].Light(), Is.EqualTo(0.05f).Within(0.01f));
            Assert.That(neutralVeil.Light(), Is.LessThan(0.55f));
            Assert.That(colored.Red - colored.Blue, Is.GreaterThan(0.70f - 0.50f));
        });
    }

    [Test]
    public void ApplyInPlace_SaturationFilterNegativeAdjustmentReducesGlobalSaturation()
    {
        var source = new Rgb(0.65f, 0.30f, 0.25f);
        var globalOnlySettings = new NaturalToneMapperSettings().MakeNeutral();
        globalOnlySettings.Saturation = 50f;

        var filteredSettings = new NaturalToneMapperSettings().MakeNeutral();
        filteredSettings.Saturation = 50f;
        filteredSettings.SaturationFilters =
        [
            new SaturationColorFilter
            {
                Name = "Reduce all",
                Enabled = true,
                Ranges =
                [
                    new SaturationColorRange
                    {
                        HueMin = 0f,
                        HueMax = 360f,
                        SaturationMin = 0f,
                        SaturationMax = 1f,
                        ValueMin = 0f,
                        ValueMax = 1f,
                        SaturationMultiplier = -80f
                    }
                ]
            }
        ];

        var globalOnly = new Image<Rgb>(1, 1) { Pixels = [source] };
        var filtered = new Image<Rgb>(1, 1) { Pixels = [source] };

        ToneMapperFactory.Create(globalOnlySettings).ApplyInPlace(globalOnly);
        ToneMapperFactory.Create(filteredSettings).ApplyInPlace(filtered);

        Assert.That(Chroma(filtered.Pixels[0]), Is.LessThan(Chroma(globalOnly.Pixels[0])));
    }

    [Test]
    public void ApplyInPlace_VibranceBoostsMutedColorsMoreThanSaturatedColors()
    {
        var settings = new NaturalToneMapperSettings().MakeNeutral();
        settings.Saturation = 80f;
        var mapper = ToneMapperFactory.Create(settings);
        var mutedSource = new Rgb(0.50f, 0.46f, 0.42f);
        var saturatedSource = new Rgb(0.75f, 0.18f, 0.12f);
        var image = new Image<Rgb>(2, 1)
        {
            Pixels = [mutedSource, saturatedSource]
        };

        mapper.ApplyInPlace(image);

        var mutedChromaGain = Chroma(image.Pixels[0]) / Chroma(mutedSource);
        var saturatedChromaGain = Chroma(image.Pixels[1]) / Chroma(saturatedSource);

        Assert.That(mutedChromaGain, Is.GreaterThan(saturatedChromaGain));
    }

    [Test]
    public void ApplyInPlace_SaturationFilterStrengthFallsOffNearRangeEdge()
    {
        var settings = new NaturalToneMapperSettings().MakeNeutral();
        settings.SaturationFilters =
        [
            new SaturationColorFilter
            {
                Name = "Soft red",
                Enabled = true,
                Ranges =
                [
                    new SaturationColorRange
                    {
                        HueMin = 350f,
                        HueMax = 10f,
                        SaturationMin = 0f,
                        SaturationMax = 1f,
                        ValueMin = 0f,
                        ValueMax = 1f,
                        SaturationMultiplier = 100f
                    }
                ]
            }
        ];

        var centerHue = HsvToRgb(0f, 0.35f, 0.55f);
        var edgeHue = HsvToRgb(9f, 0.35f, 0.55f);
        var image = new Image<Rgb>(2, 1)
        {
            Pixels = [centerHue, edgeHue]
        };

        ToneMapperFactory.Create(settings).ApplyInPlace(image);

        Assert.That(
            Chroma(image.Pixels[0]) - Chroma(centerHue),
            Is.GreaterThan(Chroma(image.Pixels[1]) - Chroma(edgeHue)));
    }

    [Test]
    public void ApplyInPlace_SaturationFilterMatchesSourceColorBeforeToneMapping()
    {
        var source = HsvToRgb(30f, 0.55f, 0.6f);
        var unfilteredSettings = new NaturalToneMapperSettings().MakeNeutral();
        unfilteredSettings.Brightness = 0.2f;

        var filteredSettings = new NaturalToneMapperSettings().MakeNeutral();
        filteredSettings.Brightness = 0.2f;
        filteredSettings.SaturationFilters =
        [
            new SaturationColorFilter
            {
                Enabled = true,
                SaturationAdjustment = -100f,
                Ranges =
                [
                    new SaturationColorRange
                    {
                        HueMin = 20f,
                        HueMax = 40f,
                        SaturationMin = 0.45f,
                        SaturationMax = 0.65f,
                        ValueMin = 0.55f,
                        ValueMax = 0.65f,
                        SaturationMultiplier = -100f
                    }
                ]
            }
        ];

        var unfiltered = new Image<Rgb>(1, 1) { Pixels = [source] };
        var filtered = new Image<Rgb>(1, 1) { Pixels = [source] };

        ToneMapperFactory.Create(unfilteredSettings).ApplyInPlace(unfiltered);
        ToneMapperFactory.Create(filteredSettings).ApplyInPlace(filtered);

        Assert.That(Chroma(filtered.Pixels[0]), Is.LessThan(Chroma(unfiltered.Pixels[0]) * 0.2f));
    }

    [Test]
    public void ApplyInPlace_SaturationFilterMatchesSourceColorBeforeWhiteBalance()
    {
        var source = HsvToRgb(30f, 0.55f, 0.6f);
        var unfilteredSettings = new NaturalToneMapperSettings().MakeNeutral();
        unfilteredSettings.WhiteBalanceReferenceType = WhiteBalanceReferenceType.Gray;
        unfilteredSettings.WhiteBalanceReferenceColor = new Rgb(0.2f, 0.8f, 0.8f);

        var filteredSettings = new NaturalToneMapperSettings().MakeNeutral();
        filteredSettings.WhiteBalanceReferenceType = WhiteBalanceReferenceType.Gray;
        filteredSettings.WhiteBalanceReferenceColor = new Rgb(0.2f, 0.8f, 0.8f);
        filteredSettings.SaturationFilters =
        [
            new SaturationColorFilter
            {
                Enabled = true,
                SaturationAdjustment = -100f,
                Ranges =
                [
                    new SaturationColorRange
                    {
                        HueMin = 20f,
                        HueMax = 40f,
                        SaturationMin = 0.45f,
                        SaturationMax = 0.65f,
                        ValueMin = 0.55f,
                        ValueMax = 0.65f,
                        SaturationMultiplier = -100f
                    }
                ]
            }
        ];

        var unfiltered = new Image<Rgb>(1, 1) { Pixels = [source] };
        var filtered = new Image<Rgb>(1, 1) { Pixels = [source] };

        ToneMapperFactory.Create(unfilteredSettings).ApplyInPlace(unfiltered);
        ToneMapperFactory.Create(filteredSettings).ApplyInPlace(filtered);

        Assert.That(Chroma(filtered.Pixels[0]), Is.LessThan(Chroma(unfiltered.Pixels[0]) * 0.2f));
    }

    [Test]
    public void ApplyInPlaceGpu_SaturationFiltersAfterFirstAreApplied()
    {
        using var context = CreateGpuContextOrSkip();
        var source = new Rgb(0.65f, 0.30f, 0.25f);
        var oneFilterSettings = CreateSettingsWithSaturationFilters(-20f);
        var twoFilterSettings = CreateSettingsWithSaturationFilters(-20f, -40f);

        var oneFilter = ApplyGpu(context, oneFilterSettings, source);
        var twoFilters = ApplyGpu(context, twoFilterSettings, source);

        Assert.That(Chroma(twoFilters), Is.LessThan(Chroma(oneFilter)));
    }

    [Test]
    public void ApplyInPlace_ColorTemperatureUsesKelvinScale()
    {
        var source = new Rgb(0.45f, 0.45f, 0.45f);
        var warmSettings = new NaturalToneMapperSettings().MakeNeutral();
        warmSettings.ColorTemperature = 4000f;
        var coolSettings = new NaturalToneMapperSettings().MakeNeutral();
        coolSettings.ColorTemperature = 9000f;

        var warmImage = new Image<Rgb>(1, 1) { Pixels = [source] };
        var coolImage = new Image<Rgb>(1, 1) { Pixels = [source] };

        ToneMapperFactory.Create(warmSettings).ApplyInPlace(warmImage);
        ToneMapperFactory.Create(coolSettings).ApplyInPlace(coolImage);

        Assert.Multiple(() =>
        {
            Assert.That(warmImage.Pixels[0].Red, Is.GreaterThan(warmImage.Pixels[0].Blue));
            Assert.That(coolImage.Pixels[0].Blue, Is.GreaterThan(coolImage.Pixels[0].Red));
        });
    }

    [Test]
    public void ApplyInPlace_WhiteBalancerDisabled_DoesNotUseManualReference()
    {
        var source = new Rgb(0.6f, 0.5f, 0.4f);
        var disabled = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            WhiteBalanceReferenceType = WhiteBalanceReferenceType.None,
            WhiteBalanceReferenceColor = new Rgb(0.9f, 0.5f, 0.4f)
        });
        var enabled = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            WhiteBalanceReferenceType = WhiteBalanceReferenceType.White,
            WhiteBalanceReferenceColor = new Rgb(0.9f, 0.5f, 0.4f)
        });

        var disabledImage = new Image<Rgb>(1, 1) { Pixels = [source] };
        var enabledImage = new Image<Rgb>(1, 1) { Pixels = [source] };

        disabled.ApplyInPlace(disabledImage);
        enabled.ApplyInPlace(enabledImage);

        Assert.Multiple(() =>
        {
            Assert.That(enabledImage.Pixels[0].Green, Is.Not.EqualTo(disabledImage.Pixels[0].Green).Within(1e-5f));
            Assert.That(enabledImage.Pixels[0].Blue, Is.Not.EqualTo(disabledImage.Pixels[0].Blue).Within(1e-5f));
        });
    }

    [Test]
    public void ApplyInPlace_WhiteBalancerEnabledWithAuto_UsesAutomaticCorrection()
    {
        var source = new Rgb(0.7f, 0.35f, 0.2f);
        var withoutWhiteBalance = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            WhiteBalanceReferenceType = WhiteBalanceReferenceType.None
        });
        var withWhiteBalance = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            WhiteBalanceReferenceType = WhiteBalanceReferenceType.Auto
        });

        var withoutImage = new Image<Rgb>(1, 1) { Pixels = [source] };
        var withImage = new Image<Rgb>(1, 1) { Pixels = [source] };

        withoutWhiteBalance.ApplyInPlace(withoutImage);
        withWhiteBalance.ApplyInPlace(withImage);

        Assert.That(withImage.Pixels[0].Red, Is.Not.EqualTo(withoutImage.Pixels[0].Red).Within(1e-5f));
    }

    [Test]
    public void ApplyInPlace_TonalRangeCompression_StrongerCompressionReducesHighlights()
    {
        var source = new Rgb(6.0f, 4.0f, 2.0f);
        var weakCompression = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            BypassToneCompressionForLdr = false,
            AutoBrightnessCompensation = false,
            TonalRangeCompression = 0.5f,
            Gamma = 1f
        });
        var strongCompression = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            BypassToneCompressionForLdr = false,
            AutoBrightnessCompensation = false,
            TonalRangeCompression = 2.0f,
            Gamma = 1f
        });

        var weakImage = new Image<Rgb>(1, 1) { Pixels = [source] };
        var strongImage = new Image<Rgb>(1, 1) { Pixels = [source] };

        weakCompression.ApplyInPlace(weakImage);
        strongCompression.ApplyInPlace(strongImage);

        Assert.That(strongImage.Pixels[0].Light(), Is.LessThan(weakImage.Pixels[0].Light()));
    }

    [Test]
    public void ApplyInPlace_ToneBoostSettings_HaveNoticeableImpactAtOnePointOne()
    {
        var source = new Rgb(0.3f, 0.25f, 0.2f);
        var neutralMapper = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            BypassToneCompressionForLdr = false,
            AutoBrightnessCompensation = false,
            ShadowsBoost = 1f,
            MidtonesBoost = 1f,
            HighlightsBoost = 1f,
            Gamma = 1f
        });
        var boostedMapper = ToneMapperFactory.Create(new NaturalToneMapperSettings
        {
            BypassToneCompressionForLdr = false,
            AutoBrightnessCompensation = false,
            ShadowsBoost = 1.5f,
            MidtonesBoost = 1.5f,
            HighlightsBoost = 1.5f,
            Gamma = 1f
        });

        var neutralImage = new Image<Rgb>(2, 1) { Pixels = [source, new Rgb(2.0f, 1.5f, 1.0f)] };
        var boostedImage = new Image<Rgb>(2, 1) { Pixels = [source, new Rgb(2.0f, 1.5f, 1.0f)] };

        neutralMapper.ApplyInPlace(neutralImage);
        boostedMapper.ApplyInPlace(boostedImage);

        Assert.That(
            boostedImage.Pixels[0].Light() - neutralImage.Pixels[0].Light(),
            Is.GreaterThan(0.01f));
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

    private static Rgb ApplyGpu(GpuContext context, NaturalToneMapperSettings settings, Rgb source)
    {
        var mapper = ToneMapperFactoryGpu.Create(context, settings);
        using var pixels = context.Accelerator.Allocate1D<Rgb>(1);
        pixels.CopyFromCPU([source]);
        mapper.ApplyInPlace(pixels.View, 1, 1);
        return pixels.GetAsArray1D()[0];
    }

    private static NaturalToneMapperSettings CreateSettingsWithSaturationFilters(params float[] adjustments)
    {
        var settings = new NaturalToneMapperSettings().MakeNeutral();
        settings.Saturation = 50f;
        settings.SaturationFilters = adjustments
            .Select((adjustment, index) => new SaturationColorFilter
            {
                Name = $"Filter {index + 1}",
                Enabled = true,
                Ranges =
                [
                    new SaturationColorRange
                    {
                        HueMin = 0f,
                        HueMax = 360f,
                        SaturationMin = 0f,
                        SaturationMax = 1f,
                        ValueMin = 0f,
                        ValueMax = 1f,
                        SaturationMultiplier = adjustment
                    }
                ]
            })
            .ToArray();
        return settings;
    }
}
