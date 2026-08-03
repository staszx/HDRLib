// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Tests;

using System.Diagnostics;
using System.Drawing;
using Hdr.Debevec;
using Interfaces;
using NUnit.Framework;
using PixelProvider.ImageSharp;

public class ResponseCurveRealImageTests
{
    private const int ReliableMinimum = 8;
    private const int ReliableMaximum = 247;

    [TestCaseSource(nameof(RealBracketCases))]
    public void Select_RealBracket_ImprovesChannelCoverage(
        string firstName,
        string secondName,
        string thirdName,
        int sampleCount)
    {
        var images = LoadScaledImages([firstName, secondName, thirdName], 0.1f);
        try
        {
            var pixelInfo = images.Select(PixelInfo.Create).ToArray();
            var referenceIndex = ResponseCurveSampleSelector.SelectReferenceImageIndex(pixelInfo);
            if (referenceIndex != 0)
            {
                (pixelInfo[0], pixelInfo[referenceIndex]) = (pixelInfo[referenceIndex], pixelInfo[0]);
            }

            var selectionStart = Stopwatch.GetTimestamp();
            var selected = ResponseCurveSampleSelector.Select(pixelInfo, null, sampleCount);
            var selectionElapsed = Stopwatch.GetElapsedTime(selectionStart);
            var legacy = SelectLegacyLuminanceSamples(pixelInfo[0].Image, sampleCount);
            var selectedMetrics = MeasureSelection(pixelInfo, selected);
            var legacyMetrics = MeasureSelection(pixelInfo, legacy);
            var solveStart = Stopwatch.GetTimestamp();
            var selectedResponse = SolveResponseCurves(pixelInfo, selected);
            var solveElapsed = Stopwatch.GetElapsedTime(solveStart);
            var legacyResponse = SolveResponseCurves(pixelInfo, legacy);
            var selectedCast = MeasureNeutralRadianceCast(pixelInfo, selectedResponse.Response);
            var legacyCast = MeasureNeutralRadianceCast(pixelInfo, legacyResponse.Response);

            TestContext.Out.WriteLine(
                $"{firstName}, samples={sampleCount}: ref={pixelInfo[0].Image}, " +
                $"select={selectionElapsed.TotalMilliseconds:F1} ms, solve={solveElapsed.TotalMilliseconds:F1} ms, " +
                $"new={selectedMetrics}, legacy={legacyMetrics}, " +
                $"inliers new={selectedResponse.InlierRatio:P1}, legacy={legacyResponse.InlierRatio:P1}, " +
                $"cast new={selectedCast}, legacy={legacyCast}");

            Assert.Multiple(() =>
            {
                Assert.That(selected, Has.Count.EqualTo(sampleCount));
                Assert.That(selectedMetrics.MinimumCoveredChannelBins, Is.GreaterThanOrEqualTo(28));
                Assert.That(selectedMetrics.MinimumCoveredChannelBins, Is.GreaterThanOrEqualTo(legacyMetrics.MinimumCoveredChannelBins));
                Assert.That(selectedMetrics.SpatialCells, Is.GreaterThanOrEqualTo(56));
                Assert.That(selectedMetrics.MultiExposureRatio, Is.GreaterThanOrEqualTo(legacyMetrics.MultiExposureRatio - 0.02d));
                Assert.That(selectedCast.PixelCount, Is.GreaterThan(100));
            });
        }
        finally
        {
            foreach (var image in images)
            {
                image.Dispose();
            }
        }
    }

    private static IEnumerable<TestCaseData> RealBracketCases()
    {
        var brackets = new[]
        {
            new[] { "DSC_1986.JPG", "DSC_1987.JPG", "DSC_1988.JPG" },
            new[] { "DSC_5299.JPG", "DSC_5300.JPG", "DSC_5301.JPG" },
            new[] { "DSC_6461.JPG", "DSC_6462.JPG", "DSC_6463.JPG" },
            new[] { "DSC_7078.JPG", "DSC_7079.JPG", "DSC_7080.JPG" },
            new[] { "DSC_7313.JPG", "DSC_7314.JPG", "DSC_7315.JPG" }
        };

        foreach (var bracket in brackets)
        {
            foreach (var sampleCount in new[] { 1_000, 10_000 })
            {
                yield return new TestCaseData(bracket[0], bracket[1], bracket[2], sampleCount)
                    .SetName($"Select_RealBracket_{Path.GetFileNameWithoutExtension(bracket[0])}_{sampleCount}");
            }
        }
    }

    private static List<IImageProxy> LoadScaledImages(IReadOnlyList<string> names, float scale)
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Samples");
        var loaded = new List<ImageSharpProxy>(names.Count);
        foreach (var name in names)
        {
            var image = new ImageSharpProxy();
            image.Load(Path.Combine(path, name));
            var width = Math.Max(8, (int)Math.Round(image.Width * scale));
            var height = Math.Max(1, (int)Math.Round(image.Height * scale));
            width -= width % 8;
            image.ImageProcessor.Resize(width, height);
            loaded.Add(image);
        }

        var exposureTimes = new double[names.Count];
        var orderedByBrightness = Enumerable.Range(0, loaded.Count)
            .OrderBy(index => MeasureMeanLuminance(loaded[index]))
            .ToArray();
        var relativeExposureTimes = new[] { 0.25d, 1d, 4d };
        for (var rank = 0; rank < orderedByBrightness.Length; rank++)
        {
            exposureTimes[orderedByBrightness[rank]] = relativeExposureTimes[rank];
        }

        var result = new List<IImageProxy>(loaded.Count);
        for (var index = 0; index < loaded.Count; index++)
        {
            result.Add(new ExposureOverrideProxy(loaded[index], names[index], exposureTimes[index]));
        }

        return result;
    }

    private static double MeasureMeanLuminance(IImageProxy image)
    {
        double sum = 0d;
        long count = 0;
        for (var y = 0; y < image.Height; y += 4)
        {
            var row = image.LoadRow(y);
            for (var x = 0; x < image.Width; x += 4)
            {
                var offset = x * Const.ChannelCount;
                sum +=
                    (0.2126d * row[offset]) +
                    (0.7152d * row[offset + 1]) +
                    (0.0722d * row[offset + 2]);
                count++;
            }
        }

        return sum / Math.Max(count, 1);
    }

    private static SelectionMetrics MeasureSelection(PixelInfo[] images, IReadOnlyList<Point> samples)
    {
        var coveredBins = new HashSet<int>[Const.ChannelCount];
        for (var channel = 0; channel < coveredBins.Length; channel++)
        {
            coveredBins[channel] = [];
        }

        var spatialCells = new HashSet<int>();
        var multiExposureSamples = 0;
        var clippedObservations = 0;
        var observationCount = 0;
        var neutralSamples = 0;
        double normalizedChromaSum = 0d;
        var width = images[0].Image.Width;
        var height = images[0].Image.Height;

        foreach (var sample in samples)
        {
            var referencePixel = images[0].Image.GetPixel(sample.X, sample.Y);
            var referenceMinimum = referencePixel.Min();
            var referenceMaximum = referencePixel.Max();
            var referenceLuminance =
                (0.2126d * referencePixel[0]) +
                (0.7152d * referencePixel[1]) +
                (0.0722d * referencePixel[2]);
            var normalizedChroma =
                (referenceMaximum - referenceMinimum) / Math.Max(referenceLuminance, 1d);
            normalizedChromaSum += normalizedChroma;
            if (normalizedChroma <= 0.10d)
            {
                neutralSamples++;
            }

            var allChannelsHaveTwoExposures = true;
            for (var channel = 0; channel < Const.ChannelCount; channel++)
            {
                var reliableCount = 0;
                foreach (var image in images)
                {
                    var value = image.Image.GetPixel(sample.X, sample.Y)[channel];
                    observationCount++;
                    if (value is < ReliableMinimum or > ReliableMaximum)
                    {
                        clippedObservations++;
                        continue;
                    }

                    reliableCount++;
                    coveredBins[channel].Add(value / 8);
                }

                allChannelsHaveTwoExposures &= reliableCount >= 2;
            }

            if (allChannelsHaveTwoExposures)
            {
                multiExposureSamples++;
            }

            var cellX = Math.Min(7, sample.X * 8 / Math.Max(width, 1));
            var cellY = Math.Min(7, sample.Y * 8 / Math.Max(height, 1));
            spatialCells.Add((cellY * 8) + cellX);
        }

        return new SelectionMetrics(
            coveredBins.Min(bins => bins.Count),
            spatialCells.Count,
            samples.Count == 0 ? 0d : (double)multiExposureSamples / samples.Count,
            observationCount == 0 ? 0d : (double)clippedObservations / observationCount,
            samples.Count == 0 ? 0d : (double)neutralSamples / samples.Count,
            samples.Count == 0 ? 0d : normalizedChromaSum / samples.Count);
    }

    private static CurveSolveMetrics SolveResponseCurves(PixelInfo[] images, List<Point> samples)
    {
        foreach (var image in images)
        {
            image.LoadSamples(samples);
        }

        var weights = HDRProcessor<ImageSharpProxy>.LutW;
        var response = new double[Const.ChannelCount][];
        for (var channel = 0; channel < Const.ChannelCount; channel++)
        {
            response[channel] = HDRProcessor<ImageSharpProxy>.GSolve(
                images,
                smoothFactor: 300,
                channel,
                weights,
                useAvx: false);
        }

        var inliers = HDRProcessor<ImageSharpProxy>.BuildResponseCurveInlierMask(images, response, weights);
        var inlierRatio = inliers.Length == 0 ? 1d : (double)inliers.Count(inlier => inlier) / inliers.Length;
        if (inliers.Any(inlier => !inlier))
        {
            for (var channel = 0; channel < Const.ChannelCount; channel++)
            {
                response[channel] = HDRProcessor<ImageSharpProxy>.GSolve(
                    images,
                    smoothFactor: 300,
                    channel,
                    weights,
                    useAvx: false,
                    inliers);
            }
        }

        return new CurveSolveMetrics(response, inlierRatio);
    }

    private static RadianceCastMetrics MeasureNeutralRadianceCast(PixelInfo[] images, double[][] response)
    {
        var weights = HDRProcessor<ImageSharpProxy>.LutW;
        var reference = images[0].Image;
        long count = 0;
        double signedRedGreen = 0d;
        double signedBlueGreen = 0d;
        double absoluteCast = 0d;
        Span<double> logRadiance = stackalloc double[Const.ChannelCount];
        for (var y = 0; y < reference.Height; y++)
        {
            var referenceRow = reference.LoadRow(y);
            var rows = images.Select(image => image.Image.LoadRow(y)).ToArray();
            for (var x = 0; x < reference.Width; x++)
            {
                var offset = x * Const.ChannelCount;
                var referenceRed = referenceRow[offset];
                var referenceGreen = referenceRow[offset + 1];
                var referenceBlue = referenceRow[offset + 2];
                var minimum = Math.Min(referenceRed, Math.Min(referenceGreen, referenceBlue));
                var maximum = Math.Max(referenceRed, Math.Max(referenceGreen, referenceBlue));
                var luminance =
                    (0.2126d * referenceRed) +
                    (0.7152d * referenceGreen) +
                    (0.0722d * referenceBlue);
                if (maximum - minimum > 4 || luminance is < 16d or > 239d)
                {
                    continue;
                }

                for (var channel = 0; channel < Const.ChannelCount; channel++)
                {
                    var weightedValue = 0d;
                    var weightSum = 0d;
                    for (var exposure = 0; exposure < rows.Length; exposure++)
                    {
                        var row = rows[exposure];
                        var colorWeight = Math.Min(
                            weights[row[offset]],
                            Math.Min(weights[row[offset + 1]], weights[row[offset + 2]]));
                        weightedValue +=
                            (response[channel][row[offset + channel]] - images[exposure].AvgLuminance) *
                            colorWeight;
                        weightSum += colorWeight;
                    }

                    logRadiance[channel] = weightedValue / Math.Max(weightSum, 1e-12d);
                }

                var redGreen = logRadiance[0] - logRadiance[1];
                var blueGreen = logRadiance[2] - logRadiance[1];
                signedRedGreen += redGreen;
                signedBlueGreen += blueGreen;
                absoluteCast += Math.Sqrt((redGreen * redGreen) + (blueGreen * blueGreen));
                count++;
            }
        }

        return count == 0
            ? default
            : new RadianceCastMetrics(
                count,
                signedRedGreen / count,
                signedBlueGreen / count,
                absoluteCast / count);
    }

    private static List<Point> SelectLegacyLuminanceSamples(
        IImageProxy image,
        int totalSamples,
        int bins = 64,
        int seed = 42)
    {
        var random = new Random(seed);
        var buckets = Enumerable.Range(0, bins).Select(_ => new List<Point>()).ToArray();
        for (var y = 0; y < image.Height; y++)
        {
            var row = image.LoadRow(y);
            for (var x = 0; x < image.Width; x++)
            {
                var offset = x * Const.ChannelCount;
                var luminance =
                    (0.2126f * row[offset]) +
                    (0.7152f * row[offset + 1]) +
                    (0.0722f * row[offset + 2]);
                var bin = (int)(luminance / 255f * (bins - 1));
                buckets[bin].Add(new Point(x, y));
            }
        }

        var result = new List<Point>(totalSamples);
        var selected = new HashSet<long>();
        var perBin = totalSamples / bins;
        var remainder = totalSamples % bins;
        for (var bin = 0; bin < bins; bin++)
        {
            var bucket = buckets[bin];
            for (var i = bucket.Count - 1; i > 0; i--)
            {
                var replacement = random.Next(i + 1);
                (bucket[i], bucket[replacement]) = (bucket[replacement], bucket[i]);
            }

            var take = Math.Min(perBin + (bin < remainder ? 1 : 0), bucket.Count);
            for (var i = 0; i < take; i++)
            {
                AddUnique(bucket[i], result, selected);
            }
        }

        for (var attempt = 0; attempt < totalSamples * 2 && result.Count < totalSamples; attempt++)
        {
            var bucket = buckets[random.Next(bins)];
            if (bucket.Count > 0)
            {
                AddUnique(bucket[random.Next(bucket.Count)], result, selected);
            }
        }

        return result;
    }

    private static void AddUnique(Point point, List<Point> result, HashSet<long> selected)
    {
        var key = ((long)point.Y << 32) | (uint)point.X;
        if (selected.Add(key))
        {
            result.Add(point);
        }
    }

    private readonly record struct SelectionMetrics(
        int MinimumCoveredChannelBins,
        int SpatialCells,
        double MultiExposureRatio,
        double ClippedObservationRatio,
        double NeutralSampleRatio,
        double MeanNormalizedChroma)
    {
        public override string ToString() =>
            $"bins={this.MinimumCoveredChannelBins}, cells={this.SpatialCells}, " +
            $"multi={this.MultiExposureRatio:P1}, clipped={this.ClippedObservationRatio:P1}, " +
            $"neutral={this.NeutralSampleRatio:P1}, chroma={this.MeanNormalizedChroma:F3}";
    }

    private readonly record struct CurveSolveMetrics(double[][] Response, double InlierRatio);

    private readonly record struct RadianceCastMetrics(
        long PixelCount,
        double RedGreen,
        double BlueGreen,
        double Magnitude)
    {
        public override string ToString() =>
            $"R/G={Math.Exp(this.RedGreen):F3}, B/G={Math.Exp(this.BlueGreen):F3}, magnitude={this.Magnitude:F3}";
    }

    private sealed class ExposureOverrideProxy : IImageProxy
    {
        private readonly IImageProxy inner;
        private readonly string name;

        public ExposureOverrideProxy(IImageProxy inner, string name, double exposureTime)
        {
            this.inner = inner;
            this.name = name;
            this.ExposureTime = exposureTime;
        }

        public int Width => this.inner.Width;

        public int Height => this.inner.Height;

        public double? ExposureTime { get; }

        public double? FNumber => 1d;

        public double? ShutterSpeedValue => null;

        public double? AppertureValue => null;

        public double? IsoSpeedRating => 100d;

        public double? ExposureBiasValue => 0d;

        public string? CameraMake => this.inner.CameraMake;

        public string? CameraModel => this.inner.CameraModel;

        public IImageProcessor ImageProcessor => this.inner.ImageProcessor;

        public void SaveAsJpeg(string fileName) => this.inner.SaveAsJpeg(fileName);

        public void SaveAsJpeg(Stream stream) => this.inner.SaveAsJpeg(stream);

        public byte[] LoadRow(int row) => this.inner.LoadRow(row);

        public void SaveRow(int row, byte[] pixels) => this.inner.SaveRow(row, pixels);

        public byte[] GetPixel(int x, int y) => this.inner.GetPixel(x, y);

        public void SetPixel(int x, int y, byte[] pixel) => this.inner.SetPixel(x, y, pixel);

        public void Load(Stream stream) => this.inner.Load(stream);

        public void Load(string fileName) => this.inner.Load(fileName);

        public void Create(int width, int height) => this.inner.Create(width, height);

        public IImageProxy Clone(HDRLib.Align.Rectangle rectangle) => this.inner.Clone(rectangle);

        public IImageProxy Clone() => this.inner.Clone();

        public void LoadFullImage(Span<byte> bytes) => this.inner.LoadFullImage(bytes);

        public void SaveFullImage(byte[] bytes) => this.inner.SaveFullImage(bytes);

        public void Dispose() => this.inner.Dispose();

        public override string ToString() => this.name;
    }
}
