// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Hdr.Debevec;

using System.Drawing;
using System.Numerics;
using HDRLib.Interfaces;

internal static class ResponseCurveSampleSelector
{
    private const int ChannelBinCount = 32;
    private const int LuminanceBinCount = 32;
    private const int SpatialGridSize = 8;
    private const byte ReliableMinimum = 8;
    private const byte ReliableMaximum = 247;

    internal static int SelectReferenceImageIndex(PixelInfo[] images)
    {
        if (images.Length <= 1)
        {
            return 0;
        }

        var validExposureIndices = Enumerable.Range(0, images.Length)
            .Where(i => images[i].ExposureTime > 0d &&
                        images[i].FNumber > 0d &&
                        double.IsFinite(images[i].AvgLuminance))
            .OrderBy(i => images[i].AvgLuminance)
            .ToArray();

        if (validExposureIndices.Length == images.Length)
        {
            var medianLogExposure = validExposureIndices.Length % 2 == 1
                ? images[validExposureIndices[validExposureIndices.Length / 2]].AvgLuminance
                : (images[validExposureIndices[(validExposureIndices.Length / 2) - 1]].AvgLuminance +
                   images[validExposureIndices[validExposureIndices.Length / 2]].AvgLuminance) * 0.5d;

            return validExposureIndices
                .OrderBy(i => Math.Abs(images[i].AvgLuminance - medianLogExposure))
                .ThenByDescending(i => CountWellExposedPixels(images[i].Image))
                .First();
        }

        return Enumerable.Range(0, images.Length)
            .OrderByDescending(i => CountWellExposedPixels(images[i].Image))
            .ThenBy(i => DistanceFromMidGray(images[i].Image))
            .First();
    }

    internal static List<Point> Select(
        PixelInfo[] images,
        float[,]? motionMask,
        int totalSamples,
        float motionThreshold = 0.6f,
        int seed = 42)
    {
        if (images.Length == 0 || totalSamples <= 0)
        {
            return [];
        }

        var width = images[0].Image.Width;
        var height = images[0].Image.Height;
        var reservoirCapacity = Math.Max(16, ((totalSamples + ChannelBinCount - 1) / ChannelBinCount) * 4);
        var channelBuckets = CreateBuckets(Const.ChannelCount * ChannelBinCount, reservoirCapacity);
        var luminanceBuckets = CreateBuckets(LuminanceBinCount, reservoirCapacity);
        var random = new Random(seed);

        for (var y = 0; y < height; y++)
        {
            var rows = new byte[images.Length][];
            for (var exposure = 0; exposure < images.Length; exposure++)
            {
                rows[exposure] = images[exposure].Image.LoadRow(y);
            }

            for (var x = 0; x < width; x++)
            {
                if (motionMask is not null && motionMask[y, x] <= motionThreshold)
                {
                    continue;
                }

                var offset = x * Const.ChannelCount;
                var redMask = BuildChannelMask(rows, offset, 0, out var redReliable);
                var greenMask = BuildChannelMask(rows, offset, 1, out var greenReliable);
                var blueMask = BuildChannelMask(rows, offset, 2, out var blueReliable);
                if (redMask == 0 || greenMask == 0 || blueMask == 0)
                {
                    continue;
                }

                var referenceRow = rows[0];
                var luminance =
                    (0.2126f * referenceRow[offset]) +
                    (0.7152f * referenceRow[offset + 1]) +
                    (0.0722f * referenceRow[offset + 2]);
                var luminanceBin = Math.Min(LuminanceBinCount - 1, (int)(luminance * LuminanceBinCount / 256f));
                var cellX = Math.Min(SpatialGridSize - 1, x * SpatialGridSize / Math.Max(width, 1));
                var cellY = Math.Min(SpatialGridSize - 1, y * SpatialGridSize / Math.Max(height, 1));
                var candidate = new Candidate(
                    new Point(x, y),
                    redMask,
                    greenMask,
                    blueMask,
                    (byte)luminanceBin,
                    (byte)((cellY * SpatialGridSize) + cellX),
                    (byte)Math.Min(redReliable, Math.Min(greenReliable, blueReliable)));

                AddToChannelBuckets(channelBuckets, candidate, 0, redMask, random);
                AddToChannelBuckets(channelBuckets, candidate, 1, greenMask, random);
                AddToChannelBuckets(channelBuckets, candidate, 2, blueMask, random);
                luminanceBuckets[luminanceBin].Consider(candidate, random);
            }
        }

        SortBuckets(channelBuckets);
        SortBuckets(luminanceBuckets);
        return ChooseBalancedSamples(channelBuckets, luminanceBuckets, totalSamples);
    }

    private static uint BuildChannelMask(byte[][] rows, int offset, int channel, out int reliableCount)
    {
        var mask = 0u;
        reliableCount = 0;
        foreach (var row in rows)
        {
            var value = row[offset + channel];
            if (value < ReliableMinimum || value > ReliableMaximum)
            {
                continue;
            }

            mask |= 1u << Math.Min(ChannelBinCount - 1, value * ChannelBinCount / 256);
            reliableCount++;
        }

        return mask;
    }

    private static void AddToChannelBuckets(Bucket[] buckets, Candidate candidate, int channel, uint mask, Random random)
    {
        var selectedBin = -1;
        var minimumSeen = int.MaxValue;
        while (mask != 0)
        {
            var bin = BitOperations.TrailingZeroCount(mask);
            var bucket = buckets[(channel * ChannelBinCount) + bin];
            if (bucket.Seen < minimumSeen)
            {
                minimumSeen = bucket.Seen;
                selectedBin = bin;
            }

            mask &= mask - 1;
        }

        if (selectedBin >= 0)
        {
            buckets[(channel * ChannelBinCount) + selectedBin].Consider(candidate, random);
        }
    }

    private static List<Point> ChooseBalancedSamples(Bucket[] channelBuckets, Bucket[] luminanceBuckets, int totalSamples)
    {
        var result = new List<Point>(totalSamples);
        var selected = new HashSet<long>();
        var channelCoverage = new int[Const.ChannelCount * ChannelBinCount];
        var luminanceCoverage = new int[LuminanceBinCount];
        var spatialCoverage = new int[SpatialGridSize * SpatialGridSize];
        var channelCursors = new int[channelBuckets.Length];
        var luminanceCursors = new int[luminanceBuckets.Length];
        var exhausted = new bool[channelBuckets.Length];
        var spatialLimit = Math.Max(1, ((totalSamples + spatialCoverage.Length - 1) / spatialCoverage.Length) * 2);

        while (result.Count < totalSamples)
        {
            var bucketIndex = FindLeastCoveredBucket(channelCoverage, exhausted);
            if (bucketIndex < 0)
            {
                break;
            }

            if (!TryTake(
                    channelBuckets[bucketIndex],
                    ref channelCursors[bucketIndex],
                    selected,
                    spatialCoverage,
                    spatialLimit,
                    out var candidate))
            {
                exhausted[bucketIndex] = true;
                continue;
            }

            AddCandidate(candidate, result, channelCoverage, luminanceCoverage, spatialCoverage);
        }

        while (result.Count < totalSamples)
        {
            var bucketIndex = FindLeastCoveredBucket(luminanceCoverage, null);
            if (bucketIndex < 0)
            {
                break;
            }

            if (!TryTake(
                    luminanceBuckets[bucketIndex],
                    ref luminanceCursors[bucketIndex],
                    selected,
                    spatialCoverage,
                    int.MaxValue,
                    out var candidate))
            {
                luminanceCoverage[bucketIndex] = int.MaxValue;
                if (luminanceCoverage.All(value => value == int.MaxValue))
                {
                    break;
                }

                continue;
            }

            AddCandidate(candidate, result, channelCoverage, luminanceCoverage, spatialCoverage);
        }

        return result;
    }

    private static bool TryTake(
        Bucket bucket,
        ref int cursor,
        HashSet<long> selected,
        int[] spatialCoverage,
        int spatialLimit,
        out Candidate candidate)
    {
        while (cursor < bucket.Items.Count)
        {
            candidate = bucket.Items[cursor++];
            var key = ((long)candidate.Position.Y << 32) | (uint)candidate.Position.X;
            if (selected.Contains(key) || spatialCoverage[candidate.SpatialCell] >= spatialLimit)
            {
                continue;
            }

            selected.Add(key);
            return true;
        }

        candidate = default;
        return false;
    }

    private static void AddCandidate(
        Candidate candidate,
        List<Point> result,
        int[] channelCoverage,
        int[] luminanceCoverage,
        int[] spatialCoverage)
    {
        result.Add(candidate.Position);
        IncrementCoverage(channelCoverage, 0, candidate.RedMask);
        IncrementCoverage(channelCoverage, 1, candidate.GreenMask);
        IncrementCoverage(channelCoverage, 2, candidate.BlueMask);
        luminanceCoverage[candidate.LuminanceBin]++;
        spatialCoverage[candidate.SpatialCell]++;
    }

    private static void IncrementCoverage(int[] coverage, int channel, uint mask)
    {
        while (mask != 0)
        {
            var bin = BitOperations.TrailingZeroCount(mask);
            coverage[(channel * ChannelBinCount) + bin]++;
            mask &= mask - 1;
        }
    }

    private static int FindLeastCoveredBucket(int[] coverage, bool[]? exhausted)
    {
        var result = -1;
        var minimum = int.MaxValue;
        for (var i = 0; i < coverage.Length; i++)
        {
            if (exhausted is not null && exhausted[i])
            {
                continue;
            }

            if (coverage[i] < minimum)
            {
                minimum = coverage[i];
                result = i;
            }
        }

        return result;
    }

    private static Bucket[] CreateBuckets(int count, int capacity)
    {
        var buckets = new Bucket[count];
        for (var i = 0; i < buckets.Length; i++)
        {
            buckets[i] = new Bucket(capacity);
        }

        return buckets;
    }

    private static void SortBuckets(Bucket[] buckets)
    {
        foreach (var bucket in buckets)
        {
            bucket.Items.Sort(static (left, right) => right.Quality.CompareTo(left.Quality));
        }
    }

    private static long CountWellExposedPixels(IImageProxy image)
    {
        var step = Math.Max(1, Math.Min(image.Width, image.Height) / 256);
        long result = 0;
        for (var y = 0; y < image.Height; y += step)
        {
            var row = image.LoadRow(y);
            for (var x = 0; x < image.Width; x += step)
            {
                var offset = x * Const.ChannelCount;
                if (row[offset] is >= ReliableMinimum and <= ReliableMaximum &&
                    row[offset + 1] is >= ReliableMinimum and <= ReliableMaximum &&
                    row[offset + 2] is >= ReliableMinimum and <= ReliableMaximum)
                {
                    result++;
                }
            }
        }

        return result;
    }

    private static double DistanceFromMidGray(IImageProxy image)
    {
        var step = Math.Max(1, Math.Min(image.Width, image.Height) / 256);
        double total = 0d;
        long count = 0;
        for (var y = 0; y < image.Height; y += step)
        {
            var row = image.LoadRow(y);
            for (var x = 0; x < image.Width; x += step)
            {
                var offset = x * Const.ChannelCount;
                var luminance =
                    (0.2126d * row[offset]) +
                    (0.7152d * row[offset + 1]) +
                    (0.0722d * row[offset + 2]);
                total += Math.Abs(luminance - 128d);
                count++;
            }
        }

        return count == 0 ? double.MaxValue : total / count;
    }

    private readonly record struct Candidate(
        Point Position,
        uint RedMask,
        uint GreenMask,
        uint BlueMask,
        byte LuminanceBin,
        byte SpatialCell,
        byte Quality);

    private sealed class Bucket
    {
        private readonly int capacity;
        private int seen;

        public Bucket(int capacity)
        {
            this.capacity = capacity;
            this.Items = new List<Candidate>(capacity);
        }

        public List<Candidate> Items { get; }

        public int Seen => this.seen;

        public void Consider(Candidate candidate, Random random)
        {
            this.seen++;
            if (this.Items.Count < this.capacity)
            {
                this.Items.Add(candidate);
                return;
            }

            var replacement = random.Next(this.seen);
            if (replacement < this.capacity)
            {
                this.Items[replacement] = candidate;
            }
        }
    }
}
