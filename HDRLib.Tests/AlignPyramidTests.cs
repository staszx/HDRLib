// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Tests;

using HDRLib.Align;
using HDRLib.Interfaces;
using HDRLib.PixelProvider.ImageSharp;
using NUnit.Framework;

public class AlignPyramidTests
{
    [Test]
    public void ComputeMedianThreshold_UsesActualMedianInsteadOfAverage()
    {
        var grayscale = new byte[] { 0, 0, 0, 255, 255 };
        var validityMask = new byte[] { 1, 1, 1, 1, 1 };

        var threshold = AlignBitmapLevel.ComputeMedianThreshold(grayscale, validityMask);

        Assert.That(threshold, Is.EqualTo(0));
    }

    [Test]
    public void AlignBitmapLevel_InvalidPixelsDoNotParticipateInMaskOrMedian()
    {
        var grayscale = new byte[] { 0, 100, 200 };
        var validityMask = new byte[] { 0, 1, 1 };

        var level = new AlignBitmapLevel(3, 1, grayscale, validityMask);

        Assert.Multiple(() =>
        {
            Assert.That(level.MedianThreshold, Is.EqualTo(100));
            Assert.That(level.Mask[0], Is.EqualTo(0));
            Assert.That(level.Mask[1], Is.EqualTo(0));
            Assert.That(level.Mask[2], Is.EqualTo(1));
        });
    }

    [TestCase(7, -5)]
    [TestCase(-6, 4)]
    public void ImageAligner_Process_CorrectsKnownTranslation(int shiftX, int shiftY)
    {
        using var reference = CreateSyntheticImage(160, 120);
        using var shifted = ShiftImage(reference, shiftX, shiftY);
        var before = MeanAbsoluteDifference(reference, shifted, 16);
        var images = new List<IImageProxy> { reference.Clone(), shifted.Clone() };

        ImageAligner.Create().Process(images);

        using var aligned = images[1];
        var after = MeanAbsoluteDifference(reference, aligned, 16);

        Assert.Multiple(() =>
        {
            Assert.That(after, Is.LessThan(before * 0.35), $"before={before:F2}, after={after:F2}");
            Assert.That(after, Is.LessThan(8.0), $"aligned image should be close to the reference; before={before:F2}, after={after:F2}");
        });

        images[0].Dispose();
    }

    [Test]
    public void ImageAligner_Process_RefinesTranslationThroughAllPyramidLevels()
    {
        using var scene = CreateRandomImage(704, 544);
        using var reference = scene.Clone(new Rectangle(32, 32, 640, 480));
        using var shifted = scene.Clone(new Rectangle(19, 41, 640, 480));
        var before = MeanAbsoluteDifference(reference, shifted, 24);
        var images = new List<IImageProxy> { reference.Clone(), shifted.Clone() };

        ImageAligner.Create().Process(images);

        using var aligned = images[1];
        var after = MeanAbsoluteDifference(reference, aligned, 24);

        Assert.Multiple(() =>
        {
            Assert.That(after, Is.LessThan(before * 0.35), $"before={before:F2}, after={after:F2}");
            Assert.That(after, Is.LessThan(8.0), $"all pyramid levels must refine the full-resolution translation; before={before:F2}, after={after:F2}");
        });

        images[0].Dispose();
    }

    [Test]
    public void ImageAlignmentResampler_OptionallyFillsExposedPixelsFromReference()
    {
        using var source = CreateSolidImage(12, 8, 180, 40, 20);
        using var reference = CreateSolidImage(12, 8, 15, 80, 160);
        var transform = new AlignmentTransform(3, 0, 0, 0);

        using var blackOutside = ImageAlignmentResampler.Apply(source, transform);
        using var referenceOutside = ImageAlignmentResampler.Apply(source, transform, reference);

        Assert.Multiple(() =>
        {
            Assert.That(blackOutside.GetPixel(0, 4), Is.EqualTo(new byte[] { 0, 0, 0 }));
            Assert.That(referenceOutside.GetPixel(0, 4), Is.EqualTo(reference.GetPixel(0, 4)));
            Assert.That(referenceOutside.GetPixel(6, 4), Is.EqualTo(source.GetPixel(3, 4)));
        });
    }

    private static ImageSharpProxy CreateSolidImage(int width, int height, byte red, byte green, byte blue)
    {
        var image = new ImageSharpProxy();
        image.Create(width, height);
        for (var y = 0; y < height; y++)
        {
            var row = new byte[width * 3];
            for (var x = 0; x < width; x++)
            {
                var offset = x * 3;
                row[offset] = red;
                row[offset + 1] = green;
                row[offset + 2] = blue;
            }

            image.SaveRow(y, row);
        }

        return image;
    }

    private static ImageSharpProxy CreateRandomImage(int width, int height)
    {
        var image = new ImageSharpProxy();
        image.Create(width, height);
        var random = new Random(12345);
        for (var y = 0; y < height; y++)
        {
            var row = new byte[width * 3];
            for (var x = 0; x < width; x++)
            {
                var value = (byte)random.Next(16, 240);
                var offset = x * 3;
                row[offset] = value;
                row[offset + 1] = value;
                row[offset + 2] = value;
            }

            image.SaveRow(y, row);
        }

        return image;
    }

    private static ImageSharpProxy CreateSyntheticImage(int width, int height)
    {
        var image = new ImageSharpProxy();
        image.Create(width, height);
        for (var y = 0; y < height; y++)
        {
            var row = new byte[width * 3];
            for (var x = 0; x < width; x++)
            {
                var value = (byte)((((x * 13) ^ (y * 29)) + ((x / 11) * 37) + ((y / 7) * 19)) & 0xff);
                if ((x - 42) * (x - 42) + (y - 55) * (y - 55) < 18 * 18)
                {
                    value = 235;
                }
                else if (x > 95 && x < 135 && y > 20 && y < 52)
                {
                    value = 32;
                }

                var offset = x * 3;
                row[offset] = value;
                row[offset + 1] = value;
                row[offset + 2] = value;
            }

            image.SaveRow(y, row);
        }

        return image;
    }

    private static ImageSharpProxy ShiftImage(IImageProxy source, int shiftX, int shiftY)
    {
        var image = new ImageSharpProxy();
        image.Create(source.Width, source.Height);
        var sourceRows = new byte[source.Height][];
        for (var y = 0; y < source.Height; y++)
        {
            sourceRows[y] = source.LoadRow(y);
        }

        for (var y = 0; y < source.Height; y++)
        {
            var row = new byte[source.Width * 3];
            var srcY = y - shiftY;
            if (srcY >= 0 && srcY < source.Height)
            {
                var sourceRow = sourceRows[srcY];
                for (var x = 0; x < source.Width; x++)
                {
                    var srcX = x - shiftX;
                    if (srcX < 0 || srcX >= source.Width)
                    {
                        continue;
                    }

                    Array.Copy(sourceRow, srcX * 3, row, x * 3, 3);
                }
            }

            image.SaveRow(y, row);
        }

        return image;
    }

    private static double MeanAbsoluteDifference(IImageProxy expected, IImageProxy actual, int margin)
    {
        long total = 0;
        var count = 0;
        for (var y = margin; y < expected.Height - margin; y++)
        {
            var expectedRow = expected.LoadRow(y);
            var actualRow = actual.LoadRow(y);
            for (var x = margin; x < expected.Width - margin; x++)
            {
                var offset = x * 3;
                total += Math.Abs(expectedRow[offset] - actualRow[offset]);
                total += Math.Abs(expectedRow[offset + 1] - actualRow[offset + 1]);
                total += Math.Abs(expectedRow[offset + 2] - actualRow[offset + 2]);
                count += 3;
            }
        }

        return (double)total / count;
    }
}
