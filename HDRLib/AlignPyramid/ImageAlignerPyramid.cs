// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Align;

using System.Runtime.CompilerServices;
using Interfaces;

internal abstract class ImageAlignerPyramid : ImageAligner
{
    private const int AngleSearchLevelCount = 2;

    protected internal interface IGrayscaleData : IDisposable
    {
        byte[] GetBytes();
    }

    protected internal interface IAlignBitmapLevel : IDisposable
    {
        int Width { get; }

        int Height { get; }
    }

    private sealed class CpuGrayscaleData : IGrayscaleData
    {
        private readonly byte[] grayscale;

        public CpuGrayscaleData(byte[] grayscale)
        {
            this.grayscale = grayscale;
        }

        public byte[] GetBytes() => this.grayscale;

        public void Dispose() { }
    }

    private IReadOnlyList<IAlignBitmapLevel>? standardLevels;


    protected ImageAlignerPyramid(){ }

    protected virtual float MaxAngle => 3f;

    protected virtual float CoarseAngleStep => 1f;

    protected virtual float FineAngleStep => 0.1f;

    protected virtual int LocalSearchRadius => 4;

    protected virtual bool ProcessCandidatesInParallel => false;

    public override void Process(IList<IImageProxy> images)
    {
        this.images = images;
        if (images.Count == 0)
        {
            throw new InvalidOperationException("ImageAlignerPyramid requires non-empty images.");
        }

        DisposeLevels(this.standardLevels);
        this.standardLevels = BuildPyramid(images[0]);

        try
        {
            if (this.ProcessCandidatesInParallel)
            {
                Parallel.For(1, images.Count, this.ProcessImage);
            }
            else
            {
                for (var index = 1; index < images.Count; index++)
                {
                    this.ProcessImage(index);
                }
            }
        }
        finally
        {
            DisposeLevels(this.standardLevels);
            this.standardLevels = null;
        }
    }

    private void ProcessImage(int index)
    {
        if (this.images is null)
        {
            throw new InvalidOperationException("Image list is not initialized.");
        }

        var source = this.images[index];
        var transform = this.EstimateTransform(source);
        var reference = this.FillOutsideWithReference ? this.images[0] : null;
        var aligned = this.ApplyTransform(source, transform, reference);
        source.Dispose();
        this.images[index] = aligned;
    }

    protected abstract AlignShiftScore FindBestShift(IAlignBitmapLevel referenceLevel, IAlignBitmapLevel candidateLevel, int centerX, int centerY, int radius);

    protected virtual IImageProxy ApplyTransform(IImageProxy source, AlignmentTransform transform, IImageProxy? reference) =>
        ImageAlignmentResampler.Apply(source, transform, reference);

    private AlignmentTransform EstimateTransform(IImageProxy image)
    {
        var candidateLevels = BuildPyramid(image);
        var coarseAngles = GetAngles(-this.MaxAngle, this.MaxAngle, this.CoarseAngleStep);
        try
        {
            var best = this.EvaluateAngles(candidateLevels, coarseAngles);
            var refineStart = MathF.Max(-this.MaxAngle, best.Angle - this.CoarseAngleStep);
            var refineEnd = MathF.Min(this.MaxAngle, best.Angle + this.CoarseAngleStep);
            var refined = this.EvaluateAngles(candidateLevels, GetAngles(refineStart, refineEnd, this.FineAngleStep));
            var selected = refined.Score < best.Score ? refined : best;
            var fullResolution = this.EvaluateSingleAngle(candidateLevels, selected.Angle);
            return this.RefineTransform(candidateLevels, fullResolution);
        }
        finally
        {
            DisposeLevels(candidateLevels);
        }
    }

    protected IReadOnlyList<IAlignBitmapLevel> StandardLevels => this.standardLevels ?? throw new InvalidOperationException("Standard pyramid levels are not initialized.");

    protected virtual AlignmentTransform RefineTransform(IReadOnlyList<IAlignBitmapLevel> candidateLevels, AlignmentTransform transform) => transform;

    protected virtual AlignmentTransform EvaluateAngles(IReadOnlyList<IAlignBitmapLevel> candidateLevels, IReadOnlyList<float> angles)
    {
        var best = new AlignmentTransform(0, 0, 0f, double.MaxValue);
        foreach (var angle in angles)
        {
            var current = this.EvaluateSingleAngleForSearch(candidateLevels, angle);
            if (current.Score < best.Score)
            {
                best = current;
            }
        }

        return best;
    }

    private AlignmentTransform EvaluateSingleAngleForSearch(IReadOnlyList<IAlignBitmapLevel> candidateLevels, float angle)
    {
        var levelCount = Math.Min(this.StandardLevels.Count, candidateLevels.Count);
        if (levelCount == 0)
        {
            return new AlignmentTransform(0, 0, angle, double.MaxValue);
        }

        var startLevel = Math.Max(0, levelCount - AngleSearchLevelCount);
        var shiftX = 0;
        var shiftY = 0;
        var score = double.MaxValue;

        for (var levelIndex = levelCount - 1; levelIndex >= startLevel; levelIndex--)
        {
            if (levelIndex < levelCount - 1)
            {
                shiftX *= 2;
                shiftY *= 2;
            }

            var reuseCandidateLevel = Math.Abs(angle) < 0.001f;
            var rotatedLevel = reuseCandidateLevel
                ? candidateLevels[levelIndex]
                : this.RotateLevel(candidateLevels[levelIndex], angle);
            try
            {
                var result = this.FindBestShift(this.StandardLevels[levelIndex], rotatedLevel, shiftX, shiftY, this.LocalSearchRadius);
                shiftX = result.X;
                shiftY = result.Y;
                score = result.NormalizedScore;
            }
            finally
            {
                if (!reuseCandidateLevel)
                {
                    rotatedLevel.Dispose();
                }
            }
        }

        for (var levelIndex = startLevel - 1; levelIndex >= 0; levelIndex--)
        {
            shiftX *= 2;
            shiftY *= 2;
        }

        return new AlignmentTransform(shiftX, shiftY, angle, score);
    }

    protected virtual AlignmentTransform EvaluateSingleAngle(IReadOnlyList<IAlignBitmapLevel> candidateLevels, float angle)
    {
        var reuseCandidateLevels = Math.Abs(angle) < 0.001f;
        var rotatedLevels = reuseCandidateLevels ? candidateLevels : this.RotatePyramid(candidateLevels, angle);
        try
        {
            var shiftX = 0;
            var shiftY = 0;
            var score = double.MaxValue;

            var levelCount = Math.Min(this.StandardLevels.Count, rotatedLevels.Count);
            if (levelCount == 0)
            {
                return new AlignmentTransform(0, 0, angle, double.MaxValue);
            }

            for (var levelIndex = levelCount - 1; levelIndex >= 0; levelIndex--)
            {
                if (levelIndex < levelCount - 1)
                {
                    shiftX *= 2;
                    shiftY *= 2;
                }

                var result = this.FindBestShift(this.StandardLevels[levelIndex], rotatedLevels[levelIndex], shiftX, shiftY, this.LocalSearchRadius);

                shiftX = result.X;
                shiftY = result.Y;
                score = result.NormalizedScore;
            }

            return new AlignmentTransform(shiftX, shiftY, angle, score);
        }
        finally
        {
            if (!reuseCandidateLevels)
            {
                DisposeLevels(rotatedLevels);
            }
        }
    }

    private static IReadOnlyList<float> GetAngles(float start, float end, float step)
    {
        var values = new List<float>();
        for (var angle = start; angle <= end + 0.001f; angle += step)
        {
            values.Add(MathF.Round(angle, 3));
        }

        return values;
    }

    protected virtual IReadOnlyList<IAlignBitmapLevel> BuildPyramid(IImageProxy image)
    {
        var levels = new List<AlignBitmapLevel>();
        using var grayscaleData = this.LoadGrayscale(image);
        var grayscale = grayscaleData.GetBytes();
        var validityMask = CreateValidityMask(grayscale.Length);
        var width = image.Width;
        var height = image.Height;
        levels.Add(new AlignBitmapLevel(width, height, grayscale, validityMask));

        while (width >= 64 && height >= 64)
        {
            (grayscale, validityMask) = Downsample(grayscale, validityMask, width, height, out width, out height);
            levels.Add(new AlignBitmapLevel(width, height, grayscale, validityMask));
        }

        return levels;
    }

    protected virtual IReadOnlyList<IAlignBitmapLevel> RotatePyramid(IReadOnlyList<IAlignBitmapLevel> levels, float angle)
    {
        var rotated = new List<AlignBitmapLevel>(levels.Count);
        foreach (var level in levels)
        {
            rotated.Add((AlignBitmapLevel)this.RotateLevel(level, angle));
        }

        return rotated;
    }

    protected virtual IAlignBitmapLevel RotateLevel(IAlignBitmapLevel level, float angle)
    {
        var cpuLevel = (AlignBitmapLevel)level;
        var rotatedPlane = RotateCpu(cpuLevel.Grayscale, cpuLevel.ValidityMask, cpuLevel.Width, cpuLevel.Height, angle);
        return new AlignBitmapLevel(cpuLevel.Width, cpuLevel.Height, rotatedPlane.Grayscale, rotatedPlane.ValidityMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    protected virtual IGrayscaleData LoadGrayscale(IImageProxy image)
    {
        var grayscale = new byte[image.Width * image.Height];
        Parallel.For(0, image.Height, y =>
        {
            var row = image.LoadRow(y);
            var rowOffset = y * image.Width;
            for (var x = 0; x < image.Width; x++)
            {
                var pixelOffset = x * 3;
                grayscale[rowOffset + x] = (byte)((54 * row[pixelOffset] + 183 * row[pixelOffset + 1] + 19 * row[pixelOffset + 2]) >> 8);
            }
        });

        return new CpuGrayscaleData(grayscale);
    }

    private static (byte[] Grayscale, byte[] ValidityMask) Downsample(byte[] source, byte[] validityMask, int width, int height, out int resultWidth,
        out int resultHeight)
    {
        resultWidth = Math.Max(1, width / 2);
        resultHeight = Math.Max(1, height / 2);
        var result = new byte[resultWidth * resultHeight];
        var resultValidity = new byte[result.Length];
        var rw = resultWidth;
        Parallel.For(0, resultHeight, y =>
        {
            var sourceY = y * 2;
            for (var x = 0; x < rw; x++)
            {
                var sourceX = x * 2;
                var sum = 0;
                var count = 0;

                AddIfValid(sourceY, sourceX);
                AddIfValid(sourceY, sourceX + 1);
                AddIfValid(sourceY + 1, sourceX);
                AddIfValid(sourceY + 1, sourceX + 1);

                var destinationIndex = y * rw + x;
                if (count > 0)
                {
                    result[destinationIndex] = (byte)(sum / count);
                    resultValidity[destinationIndex] = 1;
                }

                void AddIfValid(int sampleY, int sampleX)
                {
                    if (sampleX < 0 || sampleY < 0 || sampleX >= width || sampleY >= height)
                    {
                        return;
                    }

                    var sourceIndex = sampleY * width + sampleX;
                    if (validityMask[sourceIndex] == 0)
                    {
                        return;
                    }

                    sum += source[sourceIndex];
                    count++;
                }
            }
        });

        return (result, resultValidity);
    }

    protected static (byte[] Grayscale, byte[] ValidityMask) RotateCpu(byte[] source, byte[] validityMask, int width, int height, float angle)
    {
        var result = new byte[source.Length];
        var resultValidity = new byte[source.Length];
        var radians = -angle * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        var centerX = (width - 1) * 0.5f;
        var centerY = (height - 1) * 0.5f;

        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var dx = x - centerX;
                var dy = y - centerY;
                var srcX = cos * dx - sin * dy + centerX;
                var srcY = sin * dx + cos * dy + centerY;
                var destinationIndex = y * width + x;
                if (TrySample(source, validityMask, width, height, srcX, srcY, out var sample))
                {
                    result[destinationIndex] = sample;
                    resultValidity[destinationIndex] = 1;
                }
            }
        });

        return (result, resultValidity);
    }

    private static bool TrySample(byte[] source, byte[] validityMask, int width, int height, float x, float y, out byte sample)
    {
        var x0 = (int)MathF.Round(x);
        var y0 = (int)MathF.Round(y);
        if (x0 < 0 || y0 < 0 || x0 >= width || y0 >= height)
        {
            sample = 0;
            return false;
        }

        var sourceIndex = y0 * width + x0;
        sample = source[sourceIndex];
        return validityMask[sourceIndex] != 0;
    }

    private static byte[] CreateValidityMask(int length)
    {
        var validityMask = new byte[length];
        validityMask.AsSpan().Fill(1);
        return validityMask;
    }

    protected static void DisposeLevels(IReadOnlyList<IAlignBitmapLevel>? levels)
    {
        if (levels == null)
        {
            return;
        }

        foreach (var level in levels)
        {
            level.Dispose();
        }
    }
}
