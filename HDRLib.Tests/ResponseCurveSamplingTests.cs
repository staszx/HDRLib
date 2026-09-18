// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Tests;

using System.Drawing;
using Hdr;
using Hdr.Debevec;
using Interfaces;
using NUnit.Framework;

public class ResponseCurveSamplingTests
{
    [Test]
    public void Weight_TapersClippedValuesToZero()
    {
        Assert.Multiple(() =>
        {
            Assert.That(HDRProcessor<SyntheticImageProxy>.Weight(0), Is.Zero);
            Assert.That(HDRProcessor<SyntheticImageProxy>.Weight(255), Is.Zero);
            Assert.That(HDRProcessor<SyntheticImageProxy>.Weight(8), Is.LessThan(HDRProcessor<SyntheticImageProxy>.Weight(16)));
            Assert.That(HDRProcessor<SyntheticImageProxy>.Weight(128), Is.EqualTo(1f).Within(1e-6f));
        });
    }

    [Test]
    public void SelectReferenceImageIndex_UsesMedianExposureRegardlessOfInputOrder()
    {
        using var dark = SyntheticImageProxy.CreateConstant(8, 8, 48, exposureTime: 0.25d);
        using var middle = SyntheticImageProxy.CreateConstant(8, 8, 128, exposureTime: 0.5d);
        using var light = SyntheticImageProxy.CreateConstant(8, 8, 220, exposureTime: 1d);
        var pixels = new[]
        {
            PixelInfo.Create(light),
            PixelInfo.Create(dark),
            PixelInfo.Create(middle)
        };

        var selected = ResponseCurveSampleSelector.SelectReferenceImageIndex(pixels);

        Assert.That(pixels[selected].Image, Is.SameAs(middle));
    }

    [Test]
    public void Select_BalancesRgbCoverageAndSpatialDistribution()
    {
        const int width = 64;
        const int height = 64;
        using var reference = CreateGradient(width, height, 1f, 0.5d);
        using var dark = CreateGradient(width, height, 0.5f, 0.25d);
        using var light = CreateGradient(width, height, 1.8f, 1d);
        var pixels = new[]
        {
            PixelInfo.Create(reference),
            PixelInfo.Create(dark),
            PixelInfo.Create(light)
        };

        var selected = ResponseCurveSampleSelector.Select(pixels, null, 256);

        var occupiedCells = selected
            .Select(point => ((point.X * 8 / width), (point.Y * 8 / height)))
            .Distinct()
            .Count();
        var channelBins = Enumerable.Range(0, 3)
            .Select(channel => selected
                .SelectMany(point => pixels.Select(image => image.Image.GetPixel(point.X, point.Y)[channel]))
                .Where(value => value is >= 8 and <= 247)
                .Select(value => value / 8)
                .Distinct()
                .Count())
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(selected, Has.Count.EqualTo(256));
            Assert.That(occupiedCells, Is.GreaterThanOrEqualTo(48));
            Assert.That(channelBins, Has.All.GreaterThanOrEqualTo(24));
        });
    }

    [Test]
    public void Select_SupportsTenTimesTheDefaultSampleCount()
    {
        const int width = 128;
        const int height = 128;
        using var reference = CreateGradient(width, height, 1f, 0.5d);
        using var dark = CreateGradient(width, height, 0.5f, 0.25d);
        using var light = CreateGradient(width, height, 1.8f, 1d);
        var pixels = new[]
        {
            PixelInfo.Create(reference),
            PixelInfo.Create(dark),
            PixelInfo.Create(light)
        };

        var selected = ResponseCurveSampleSelector.Select(pixels, null, 5_000);

        Assert.That(selected, Has.Count.EqualTo(5_000));
    }

    [Test]
    public void BuildResponseCurveInlierMask_RejectsLargeCrossExposureOutlier()
    {
        const int sampleCount = 100;
        var logTimes = new[] { -1d, 0d, 1d };
        var images = new PixelInfo[logTimes.Length];
        for (var exposure = 0; exposure < logTimes.Length; exposure++)
        {
            var proxy = new SyntheticImageProxy(sampleCount, 1, Math.Exp(logTimes[exposure]));
            for (var sample = 0; sample < sampleCount; sample++)
            {
                var value = (byte)Math.Clamp((3d + logTimes[exposure]) * 32d, 0d, 255d);
                if (sample == sampleCount - 1 && exposure == 1)
                {
                    value = 220;
                }

                proxy.SetPixel(sample, 0, [value, value, value]);
            }

            images[exposure] = PixelInfo.Create(proxy);
            images[exposure].LoadSamples(Enumerable.Range(0, sampleCount).Select(x => new Point(x, 0)).ToList());
        }

        var response = Enumerable.Range(0, 3)
            .Select(_ => Enumerable.Range(0, 256).Select(value => value / 32d).ToArray())
            .ToArray();
        var weights = Enumerable.Range(0, 256)
            .Select(value => HDRProcessor<SyntheticImageProxy>.Weight(value))
            .ToArray();

        try
        {
            var inliers = HDRProcessor<SyntheticImageProxy>.BuildResponseCurveInlierMask(images, response, weights);

            Assert.Multiple(() =>
            {
                Assert.That(inliers.Take(sampleCount - 1), Has.All.True);
                Assert.That(inliers[^1], Is.False);
            });
        }
        finally
        {
            foreach (var image in images)
            {
                image.Image.Dispose();
            }
        }
    }

    [Test]
    public void CreateMotionMask_GammaEncodedStaticBracket_PreservesStaticPixels()
    {
        const int width = 64;
        const int height = 32;
        const double gamma = 2.2d;
        using var reference = CreateGammaEncodedImage(width, height, 1d, gamma);
        using var dark = CreateGammaEncodedImage(width, height, 0.5d, gamma);
        using var light = CreateGammaEncodedImage(width, height, 2d, gamma);
        var images = new[]
        {
            PixelInfo.Create(reference),
            PixelInfo.Create(dark),
            PixelInfo.Create(light)
        };
        var response = CreateGammaResponse(gamma);

        var motionMask = HDRProcessor<SyntheticImageProxy>.CreateMotionMask(images, response, 0, 50)!;

        var staticPixelRatio = motionMask.Cast<float>().Count(weight => weight > 0.6f) /
                               (double)(width * height);
        Assert.That(staticPixelRatio, Is.GreaterThan(0.98d));
    }

    [Test]
    public void CreateMotionMask_RadianceChange_RejectsChangedPixel()
    {
        const int width = 64;
        const int height = 32;
        const int changedX = width / 2;
        const int changedY = height / 2;
        const double gamma = 2.2d;
        using var reference = CreateGammaEncodedImage(width, height, 1d, gamma);
        using var dark = CreateGammaEncodedImage(width, height, 0.5d, gamma);
        using var light = CreateGammaEncodedImage(
            width,
            height,
            2d,
            gamma,
            changedX,
            changedY,
            changedRadianceScale: 0.25d);
        var images = new[]
        {
            PixelInfo.Create(reference),
            PixelInfo.Create(dark),
            PixelInfo.Create(light)
        };
        var response = CreateGammaResponse(gamma);

        var motionMask = HDRProcessor<SyntheticImageProxy>.CreateMotionMask(images, response, 0, 50)!;

        Assert.Multiple(() =>
        {
            Assert.That(motionMask[changedY, changedX], Is.LessThanOrEqualTo(0.6f));
            Assert.That(motionMask[changedY, changedX - 1], Is.GreaterThan(0.6f));
            Assert.That(motionMask[changedY, changedX + 1], Is.GreaterThan(0.6f));
        });
    }

    private static SyntheticImageProxy CreateGradient(int width, int height, float scale, double exposureTime)
    {
        var image = new SyntheticImageProxy(width, height, exposureTime);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var red = Scale((x * 255f) / (width - 1), scale);
                var green = Scale((y * 255f) / (height - 1), scale);
                var blue = Scale(((x + y) * 255f) / (width + height - 2), scale);
                image.SetPixel(x, y, [red, green, blue]);
            }
        }

        return image;
    }

    private static SyntheticImageProxy CreateGammaEncodedImage(
        int width,
        int height,
        double exposureTime,
        double gamma,
        int changedX = -1,
        int changedY = -1,
        double changedRadianceScale = 1d)
    {
        var image = new SyntheticImageProxy(width, height, exposureTime);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var radiance = 0.2d + x / (double)(width - 1);
                var scale = x == changedX && y == changedY ? changedRadianceScale : 1d;
                var red = EncodeGamma(radiance * (0.8d + 0.2d * y / (height - 1)) * scale, exposureTime, gamma);
                var green = EncodeGamma(radiance * scale, exposureTime, gamma);
                var blue = EncodeGamma(radiance * (1.1d - 0.2d * y / (height - 1)) * scale, exposureTime, gamma);
                image.SetPixel(x, y, [red, green, blue]);
            }
        }

        return image;
    }

    private static byte EncodeGamma(double radiance, double exposureTime, double gamma)
    {
        var value = 128d * Math.Pow(radiance * exposureTime, 1d / gamma);
        return (byte)Math.Clamp(Math.Round(value), 0d, 255d);
    }

    private static double[][] CreateGammaResponse(double gamma)
    {
        return Enumerable.Range(0, 3)
            .Select(_ => Enumerable.Range(0, 256)
                .Select(value => gamma * Math.Log(Math.Max(value, 1) / 128d))
                .ToArray())
            .ToArray();
    }

    private static byte Scale(float value, float scale) => (byte)Math.Clamp(MathF.Round(value * scale), 0f, 255f);

    private sealed class SyntheticImageProxy : IImageProxy
    {
        private readonly byte[] pixels;

        public SyntheticImageProxy(int width, int height, double exposureTime)
        {
            this.Width = width;
            this.Height = height;
            this.ExposureTime = exposureTime;
            this.pixels = new byte[width * height * 3];
        }

        public static SyntheticImageProxy CreateConstant(int width, int height, byte value, double exposureTime)
        {
            var image = new SyntheticImageProxy(width, height, exposureTime);
            Array.Fill(image.pixels, value);
            return image;
        }

        public int Width { get; }

        public int Height { get; }

        public double? ExposureTime { get; }

        public double? FNumber => 1d;

        public double? ShutterSpeedValue => null;

        public double? AppertureValue => null;

        public double? IsoSpeedRating => 100d;

        public double? ExposureBiasValue => 0d;

        public string? CameraMake => "Synthetic";

        public string? CameraModel => "Synthetic";

        public IImageProcessor ImageProcessor => throw new NotSupportedException();

        public void SaveAsJpeg(string fileName) => throw new NotSupportedException();

        public void SaveAsJpeg(Stream stream) => throw new NotSupportedException();

        public byte[] LoadRow(int row)
        {
            var result = new byte[this.Width * 3];
            Array.Copy(this.pixels, row * result.Length, result, 0, result.Length);
            return result;
        }

        public void SaveRow(int row, byte[] pixels) =>
            Array.Copy(pixels, 0, this.pixels, row * this.Width * 3, pixels.Length);

        public byte[] GetPixel(int x, int y)
        {
            var offset = ((y * this.Width) + x) * 3;
            return [this.pixels[offset], this.pixels[offset + 1], this.pixels[offset + 2]];
        }

        public void SetPixel(int x, int y, byte[] pixel)
        {
            var offset = ((y * this.Width) + x) * 3;
            this.pixels[offset] = pixel[0];
            this.pixels[offset + 1] = pixel[1];
            this.pixels[offset + 2] = pixel[2];
        }

        public void Load(Stream stream) => throw new NotSupportedException();

        public void Load(string fileName) => throw new NotSupportedException();

        public void Create(int width, int height) => throw new NotSupportedException();

        public IImageProxy Clone(HDRLib.Align.Rectangle rectangle) => throw new NotSupportedException();

        public IImageProxy Clone() => throw new NotSupportedException();

        public void LoadFullImage(Span<byte> bytes) => this.pixels.CopyTo(bytes);

        public void SaveFullImage(byte[] bytes) => bytes.CopyTo(this.pixels, 0);

        public void Dispose()
        {
        }
    }
}
