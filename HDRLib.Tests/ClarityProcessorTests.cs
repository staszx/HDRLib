// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Tests;

using HDRLib.Image;
using HDRLib.ToneMapping;
using HDRLib.ToneMapping.Factories;
using HDRLib.ToneMapping.Settings;
using NUnit.Framework;

public class ClarityProcessorTests
{
    [Test]
    public void Clarity_Zero_IsNeutral()
    {
        var image = CreateDetailImage();
        var original = (Rgb[])image.Pixels.Clone();
        var settings = new AcesFilmicTonemapperSettings().MakeNeutral();

        ToneMapperFactory.Create(settings).ApplyInPlace(image);

        Assert.That(image.Pixels, Is.EqualTo(original));
    }

    [Test]
    public void Clarity_Positive_IncreasesMidtoneEdgeContrast()
    {
        var image = CreateDetailImage();
        var before = EdgeContrast(image.Pixels);
        var settings = new AcesFilmicTonemapperSettings().MakeNeutral();
        settings.Clarity = 50f;

        ToneMapperFactory.Create(settings).ApplyInPlace(image);

        Assert.That(EdgeContrast(image.Pixels), Is.GreaterThan(before));
    }

    [Test]
    public void Clarity_SerializesAndParticipatesInNeutrality()
    {
        var settings = new NaturalToneMapperSettings().MakeNeutral();
        settings.Clarity = 25f;

        var restored = ToneMapperSettings.LoadFromXml(settings.ToXml());

        Assert.Multiple(() =>
        {
            Assert.That(restored.Clarity, Is.EqualTo(25f));
            Assert.That(restored.IsNeutral(), Is.False);
        });
    }

    private static HDRLib.Image.Image<Rgb> CreateDetailImage()
    {
        var pixels = new Rgb[9 * 9];
        for (var y = 0; y < 9; y++)
        {
            for (var x = 0; x < 9; x++)
            {
                var value = x < 4 ? 0.42f : 0.58f;
                pixels[(y * 9) + x] = new Rgb(value, value, value);
            }
        }

        return new HDRLib.Image.Image<Rgb>(9, 9)
        {
            Width = 9,
            Height = 9,
            Pixels = pixels
        };
    }

    private static float EdgeContrast(Rgb[] pixels)
    {
        return pixels[(4 * 9) + 4].Light() - pixels[(4 * 9) + 3].Light();
    }
}
