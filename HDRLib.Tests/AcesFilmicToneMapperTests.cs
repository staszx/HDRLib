// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Tests;

using HDRLib.Image;
using HDRLib.ToneMapping.Factories;
using HDRLib.ToneMapping.Settings;
using NUnit.Framework;

public class AcesFilmicToneMapperTests
{
    private const float Tolerance = 3e-5f;

    [Test]
    public void DefaultSettings_AreNeutral()
    {
        var settings = new AcesFilmicTonemapperSettings().MakeNeutral();

        Assert.That(settings.IsNeutral(), Is.True);
    }

    [Test]
    public void ApplyInPlace_DefaultSettingsKeepOriginalPixel()
    {
        var settings = new AcesFilmicTonemapperSettings().MakeNeutral();
        var toneMapper = ToneMapperFactory.Create(settings);
        var image = new Image<Rgb>(1, 1)
        {
            Pixels = [new Rgb(4.0f, 1.0f, 0.25f)]
        };
        var original = image.Pixels[0];

        toneMapper.ApplyInPlace(image);

        Assert.That(MeanAbsoluteDifference(original, image.Pixels[0]), Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void ApplyInPlace_KeyNearNeutralChangesSmoothly()
    {
        var original = new Image<Rgb>(1, 1)
        {
            Pixels = [new Rgb(0.45f, 0.32f, 0.2f)]
        };
        var slightlyDarker = Clone(original);
        var slightlyBrighter = Clone(original);
        var strongerBrighter = Clone(original);

        ToneMapperFactory.Create(new AcesFilmicTonemapperSettings().MakeNeutral()).ApplyInPlace(original);
        ToneMapperFactory.Create(new AcesFilmicTonemapperSettings { Key = 0.1715f, Gamma = 1f }).ApplyInPlace(slightlyDarker);
        ToneMapperFactory.Create(new AcesFilmicTonemapperSettings { Key = 0.196f, Gamma = 1f }).ApplyInPlace(slightlyBrighter);
        ToneMapperFactory.Create(new AcesFilmicTonemapperSettings { Key = 0.34f, Gamma = 1f }).ApplyInPlace(strongerBrighter);

        var darkerDelta = MeanAbsoluteDifference(original.Pixels[0], slightlyDarker.Pixels[0]);
        var brighterDelta = MeanAbsoluteDifference(original.Pixels[0], slightlyBrighter.Pixels[0]);
        var strongerDelta = MeanAbsoluteDifference(original.Pixels[0], strongerBrighter.Pixels[0]);

        Assert.Multiple(() =>
        {
            Assert.That(darkerDelta, Is.GreaterThan(0f));
            Assert.That(darkerDelta, Is.LessThan(0.05f));
            Assert.That(brighterDelta, Is.GreaterThan(0f));
            Assert.That(brighterDelta, Is.LessThan(0.05f));
            Assert.That(strongerDelta, Is.GreaterThan(brighterDelta));
            Assert.That(slightlyDarker.Pixels[0].Light(), Is.LessThan(original.Pixels[0].Light()));
            Assert.That(slightlyBrighter.Pixels[0].Light(), Is.GreaterThan(original.Pixels[0].Light()));
            Assert.That(strongerBrighter.Pixels[0].Light(), Is.GreaterThan(slightlyBrighter.Pixels[0].Light()));
        });
    }

    [Test]
    public void ApplyInPlace_MatchesAcesFittedReferenceForSaturatedPixel()
    {
        var settings = new AcesFilmicTonemapperSettings
        {
            Key = 0.5f,
            ExposureEV = 0.0f,
            Brightness = 1.0f,
            Contrast = 1.0f,
            Gamma = 2.2f
        };

        var toneMapper = ToneMapperFactory.Create(settings);
        var image = new Image<Rgb>(1, 1)
        {
            Pixels = [new Rgb(4.0f, 1.0f, 0.25f)]
        };

        toneMapper.ApplyInPlace(image);

        var expected = ReferenceToneMap(new Rgb(4.0f, 1.0f, 0.25f), settings.Key, settings.Gamma);
        Assert.Multiple(() =>
        {
            Assert.That(image.Pixels[0].Red, Is.EqualTo(expected.Red).Within(Tolerance));
            Assert.That(image.Pixels[0].Green, Is.EqualTo(expected.Green).Within(Tolerance));
            Assert.That(image.Pixels[0].Blue, Is.EqualTo(expected.Blue).Within(Tolerance));
        });
    }

    [Test]
    public void ApplyInPlace_KeepsNeutralGrayNeutral()
    {
        var settings = new AcesFilmicTonemapperSettings
        {
            Key = 0.5f,
            ExposureEV = 0.0f,
            Brightness = 1.0f,
            Contrast = 1.0f,
            Gamma = 2.2f
        };

        var toneMapper = ToneMapperFactory.Create(settings);
        var image = new Image<Rgb>(1, 1)
        {
            Pixels = [new Rgb(2.0f, 2.0f, 2.0f)]
        };

        toneMapper.ApplyInPlace(image);

        Assert.Multiple(() =>
        {
            Assert.That(image.Pixels[0].Red, Is.EqualTo(image.Pixels[0].Green).Within(Tolerance));
            Assert.That(image.Pixels[0].Green, Is.EqualTo(image.Pixels[0].Blue).Within(Tolerance));
        });
    }

    private static Rgb ReferenceToneMap(Rgb input, float key, float gamma)
    {
        var neutralExposure = 0.18f / (input.Light() + 1e-9f);
        var mapped = MapAces(input, neutralExposure * (key / 0.18f));
        var neutralMapped = MapAces(input, neutralExposure);
        mapped = new Rgb(
            input.Red + (mapped.Red - neutralMapped.Red),
            input.Green + (mapped.Green - neutralMapped.Green),
            input.Blue + (mapped.Blue - neutralMapped.Blue));

        var invGamma = 1.0f / MathF.Max(gamma, 0.1f);
        return new Rgb(
            MathF.Pow(Math.Clamp(mapped.Red, 0f, 1f), invGamma),
            MathF.Pow(Math.Clamp(mapped.Green, 0f, 1f), invGamma),
            MathF.Pow(Math.Clamp(mapped.Blue, 0f, 1f), invGamma));
    }

    private static Rgb MapAces(Rgb input, float exposure)
    {
        var r = input.Red * exposure;
        var g = input.Green * exposure;
        var b = input.Blue * exposure;

        var acesR = (r * 0.59719f) + (g * 0.35458f) + (b * 0.04823f);
        var acesG = (r * 0.07600f) + (g * 0.90834f) + (b * 0.01566f);
        var acesB = (r * 0.02840f) + (g * 0.13383f) + (b * 0.83777f);

        acesR = Fitted(acesR);
        acesG = Fitted(acesG);
        acesB = Fitted(acesB);

        return new Rgb(
            Math.Clamp((acesR * 1.60475f) + (acesG * -0.53108f) + (acesB * -0.07367f), 0.0f, 1.0f),
            Math.Clamp((acesR * -0.10208f) + (acesG * 1.10813f) + (acesB * -0.00605f), 0.0f, 1.0f),
            Math.Clamp((acesR * -0.00327f) + (acesG * -0.07276f) + (acesB * 1.07602f), 0.0f, 1.0f));
    }

    private static Image<Rgb> Clone(Image<Rgb> image)
    {
        return new Image<Rgb>(image.Width, image.Height)
        {
            Pixels = (Rgb[])image.Pixels.Clone()
        };
    }

    private static float MeanAbsoluteDifference(Rgb expected, Rgb actual)
    {
        return (MathF.Abs(expected.Red - actual.Red) +
                MathF.Abs(expected.Green - actual.Green) +
                MathF.Abs(expected.Blue - actual.Blue)) / 3f;
    }

    private static float Fitted(float value)
    {
        value = Math.Max(value, 0.0f);
        var numerator = (value * (value + 0.0245786f)) - 0.000090537f;
        var denominator = (value * ((0.983729f * value) + 0.4329510f)) + 0.238081f;
        return Math.Max(numerator / denominator, 0.0f);
    }
}
