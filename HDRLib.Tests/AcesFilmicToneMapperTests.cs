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
    public void ApplyInPlace_MatchesAcesFittedReferenceForSaturatedPixel()
    {
        var settings = new AcesFilmicTonemapperSettings
        {
            Key = 0.18f,
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
            Key = 0.18f,
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
        var exposure = key / (input.Light() + 1e-9f);
        var r = input.Red * exposure;
        var g = input.Green * exposure;
        var b = input.Blue * exposure;

        var acesR = (r * 0.59719f) + (g * 0.35458f) + (b * 0.04823f);
        var acesG = (r * 0.07600f) + (g * 0.90834f) + (b * 0.01566f);
        var acesB = (r * 0.02840f) + (g * 0.13383f) + (b * 0.83777f);

        acesR = Fitted(acesR);
        acesG = Fitted(acesG);
        acesB = Fitted(acesB);

        var mapped = new Rgb(
            Math.Clamp((acesR * 1.60475f) + (acesG * -0.53108f) + (acesB * -0.07367f), 0.0f, 1.0f),
            Math.Clamp((acesR * -0.10208f) + (acesG * 1.10813f) + (acesB * -0.00605f), 0.0f, 1.0f),
            Math.Clamp((acesR * -0.00327f) + (acesG * -0.07276f) + (acesB * 1.07602f), 0.0f, 1.0f));

        var invGamma = 1.0f / MathF.Max(gamma, 0.1f);
        return new Rgb(
            MathF.Pow(mapped.Red, invGamma),
            MathF.Pow(mapped.Green, invGamma),
            MathF.Pow(mapped.Blue, invGamma));
    }

    private static float Fitted(float value)
    {
        value = Math.Max(value, 0.0f);
        var numerator = (value * (value + 0.0245786f)) - 0.000090537f;
        var denominator = (value * ((0.983729f * value) + 0.4329510f)) + 0.238081f;
        return Math.Max(numerator / denominator, 0.0f);
    }
}
