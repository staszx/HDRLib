// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Tests;

using HDRLib.ToneMapping.Factories;
using HDRLib.ToneMapping.Settings;
using NUnit.Framework;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using HdrImage = HDRLib.Image.Image<HDRLib.Image.Rgb>;
using HdrRgb = HDRLib.Image.Rgb;

public class ToneMapperSampleOutputTests
{
    private const float MaxMeanChannelDifference = 1f / 255f;
    private const float MaxSingleChannelDifference = 1f / 255f;

    [TestCaseSource(nameof(AllToneMappers))]
    public void ApplyInPlace_LoadsSingleImageSavesResultAndStaysCloseToOriginal(ToneMapperSettings settings)
    {
        var image = LoadImage(FindRepoFile(Path.Combine("HDRLib.Tests", "Samples", "DSC_6462.JPG")));
        var original = Clone(image);
        var toneMapper = ToneMapperFactory.Create(settings);

        toneMapper.ApplyInPlace(image);

        var outputDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "ToneMapperOutputs");
        Directory.CreateDirectory(outputDirectory);

        var outputPath = Path.Combine(outputDirectory, $"{GetOutputName(settings)}.jpg");
        SaveImage(image, outputPath);

        TestContext.AddTestAttachment(outputPath);

        var comparison = Compare(original, image);
        TestContext.Out.WriteLine(
            $"{GetOutputName(settings)}: mean channel difference = {comparison.MeanChannelDifference:F4}, max channel difference = {comparison.MaxChannelDifference:F4}");

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(outputPath), Is.True);
            Assert.That(comparison.MeanChannelDifference, Is.LessThanOrEqualTo(MaxMeanChannelDifference));
            Assert.That(comparison.MaxChannelDifference, Is.LessThanOrEqualTo(MaxSingleChannelDifference));
        });
    }

    [Test]
    public void ApplyInPlace_BaseToneBoostSettingsAffectNonNaturalToneMapper()
    {
        var source = new HdrImage(3, 1)
        {
            Width = 3,
            Height = 1,
            Pixels =
            [
                new HdrRgb(0.2f, 0.2f, 0.2f),
                new HdrRgb(0.5f, 0.5f, 0.5f),
                new HdrRgb(0.8f, 0.8f, 0.8f)
            ]
        };
        var boosted = Clone(source);
        var settings = new ContrastBalancerToneMapperSettings
        {
            ShadowsBoost = 1.5f,
            MidtonesBoost = 1.0f,
            HighlightsBoost = 0.5f
        };

        ToneMapperFactory.Create(settings).ApplyInPlace(boosted);

        Assert.Multiple(() =>
        {
            Assert.That(boosted.Pixels[0].Light(), Is.GreaterThan(source.Pixels[0].Light()));
            Assert.That(boosted.Pixels[1].Light(), Is.EqualTo(source.Pixels[1].Light()).Within(1e-5f));
            Assert.That(boosted.Pixels[2].Light(), Is.LessThan(source.Pixels[2].Light()));
        });
    }

    private static IEnumerable<TestCaseData> AllToneMappers()
    {
        yield return CreateTestCase(new AcesFilmicTonemapperSettings());
        yield return CreateTestCase(new AutoAdjustTonemapperSettings());
        yield return CreateTestCase(new NaturalToneMapperSettings());
        yield return CreateTestCase(new ContrastBalancerToneMapperSettings());
        yield return CreateTestCase(new BrightnessBalancerToneMapperSettings());
    }

    private static TestCaseData CreateTestCase(ToneMapperSettings settings)
    {
        return new TestCaseData(settings)
            .SetName($"ApplyInPlace_LoadsSingleImageSavesResultAndStaysCloseToOriginal_{GetOutputName(settings)}");
    }

    private static HdrImage Clone(HdrImage source)
    {
        return new HdrImage(source.Width, source.Height)
        {
            Width = source.Width,
            Height = source.Height,
            Pixels = (HdrRgb[])source.Pixels.Clone()
        };
    }

    private static ImageComparison Compare(HdrImage expected, HdrImage actual)
    {
        Assert.That(actual.Width, Is.EqualTo(expected.Width));
        Assert.That(actual.Height, Is.EqualTo(expected.Height));

        double total = 0;
        var max = 0f;
        for (var i = 0; i < expected.Pixels.Length; i++)
        {
            var expectedPixel = expected.Pixels[i];
            var actualPixel = actual.Pixels[i];

            var red = MathF.Abs(expectedPixel.Red - actualPixel.Red);
            var green = MathF.Abs(expectedPixel.Green - actualPixel.Green);
            var blue = MathF.Abs(expectedPixel.Blue - actualPixel.Blue);

            total += red + green + blue;
            max = MathF.Max(max, MathF.Max(red, MathF.Max(green, blue)));
        }

        return new ImageComparison((float)(total / (expected.Pixels.Length * 3)), max);
    }

    private static HdrImage LoadImage(string path)
    {
        using var source = Image.Load<Rgb24>(path);
        var result = new HdrImage(source.Width, source.Height)
        {
            Width = source.Width,
            Height = source.Height
        };

        source.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var offset = y * accessor.Width;

                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    result.Pixels[offset + x] = new HdrRgb(
                        pixel.R / 255f,
                        pixel.G / 255f,
                        pixel.B / 255f);
                }
            }
        });

        return result;
    }

    private static void SaveImage(HdrImage image, string path)
    {
        using var destination = new Image<Rgb24>(image.Width, image.Height);
        destination.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var offset = y * accessor.Width;

                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = image.Pixels[offset + x];
                    row[x] = new Rgb24(
                        ToByte(pixel.Red),
                        ToByte(pixel.Green),
                        ToByte(pixel.Blue));
                }
            }
        });

        destination.SaveAsJpeg(path);
    }

    private static byte ToByte(float value)
    {
        return (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
    }

    private static string GetOutputName(ToneMapperSettings settings)
    {
        return settings.GetType().Name.Replace("Settings", string.Empty, StringComparison.Ordinal);
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(path))
            {
                return path;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Cannot find repository file '{relativePath}'.");
    }

    private readonly record struct ImageComparison(float MeanChannelDifference, float MaxChannelDifference);
}
