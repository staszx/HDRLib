// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Align;

using Gpu;
using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;
using Interfaces;
using Adjust;

public struct ScorePartial
{
    public double SumRef;
    public double SumCand;
    public double SumRef2;
    public double SumCand2;
    public double SumProd;
    public int Valid;
}

internal sealed class ImageAlignerPyramidGpu : ImageAlignerPyramid
{
    private readonly GpuContext context;
    private readonly ImageResamplerGPU resampler;
    private readonly Action<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<byte>> initKernel;
    private readonly Action<Index1D, ArrayView<byte>, ArrayView<byte>, byte, ArrayView<byte>, ArrayView<byte>> thresholdKernel;
    private readonly Action<Index1D, ArrayView<byte>, ArrayView<byte>, int, int, ArrayView<byte>, ArrayView<byte>, int, int> downsampleKernel;
    private readonly Action<Index1D, ArrayView<byte>, ArrayView<byte>, int, int, float, ArrayView<byte>, ArrayView<byte>> rotateKernel;
    private readonly Action<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<int>> histogramKernel;
    private readonly Action<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<byte>, ArrayView<byte>, int, int, ArrayView<int>, ArrayView<int>, int, ArrayView<ScorePartial>> shiftScoreKernel;
    private const int ShiftScoreThreadsPerShift = 256;
    private const int ExclusionWindow = 4;

    // Pooled buffers (lazily allocated)
    private MemoryBuffer1D<int, Stride1D.Dense>? pooledHistogram;

    protected override float FineAngleStep => 0.1f;

    public ImageAlignerPyramidGpu(GpuContext context) : base()
    {
        this.context = context;
        this.resampler = new ImageResamplerGPU(context);
        this.initKernel = context.Accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<byte>>(InitGrayscaleAndMaskKernel);
        this.thresholdKernel = context.Accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<byte>, byte, ArrayView<byte>, ArrayView<byte>>(
            BuildBitmapAndMaskKernel);
        this.downsampleKernel = context.Accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<byte>, int, int, ArrayView<byte>, ArrayView<byte>, int, int>(
            DownsampleKernel);
        this.rotateKernel = context.Accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<byte>, int, int, float, ArrayView<byte>, ArrayView<byte>>(
            RotateKernel);
        this.histogramKernel = context.Accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<int>>(HistogramKernel);
        this.shiftScoreKernel = context.Accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<byte>, ArrayView<byte>, int, int, ArrayView<int>, ArrayView<int>, int, ArrayView<ScorePartial>>(ScoreShiftPartialKernel);
    }

    protected override IImageProxy ApplyTransform(IImageProxy source, AlignmentTransform transform)
    {
        return this.resampler.Apply(source, transform);
    }

    protected override AlignmentTransform EvaluateSingleAngle(IReadOnlyList<IAlignBitmapLevel> candidateLevels, float angle)
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

    protected override AlignShiftScore FindBestShift(IAlignBitmapLevel referenceLevel, IAlignBitmapLevel candidateLevel, int centerX, int centerY, int radius)
    {
        return this.FindBestShiftForLevel(new[] { referenceLevel }, new[] { candidateLevel }, centerX, centerY, radius);
    }

    protected override AlignmentTransform RefineTransform(IReadOnlyList<IAlignBitmapLevel> candidateLevels, AlignmentTransform transform)
    {
        var reuseCandidateLevels = Math.Abs(transform.Angle) < 0.001f;
        var rotatedLevels = reuseCandidateLevels ? candidateLevels : this.RotatePyramid(candidateLevels, transform.Angle);
        try
        {
            var referenceLevel = (AlignBitmapLevelGPU)this.StandardLevels[0];
            var candidateLevel = (AlignBitmapLevelGPU)rotatedLevels[0];
            var refined = RefineSubpixelShift(referenceLevel, candidateLevel, transform.X, transform.Y);
            return new AlignmentTransform(refined.X, refined.Y, transform.Angle, refined.Score);
        }
        finally
        {
            if (!reuseCandidateLevels)
            {
                DisposeLevels(rotatedLevels);
            }
        }
    }

    private AlignShiftScore FindBestShiftForLevel(IReadOnlyList<IAlignBitmapLevel> referenceLevels, IReadOnlyList<IAlignBitmapLevel> rotatedLevels, int centerX, int centerY,
        int radius)
    {
        var levelCount = Math.Min(referenceLevels.Count, rotatedLevels.Count);
        if (levelCount == 0)
            return new AlignShiftScore(centerX, centerY, 0, 1);

        var stride = 2 * radius + 1;
        var shiftCount = stride * stride;
        var totalScores = new double[shiftCount];
        var totalValids = new long[shiftCount];

        // Precompute shifts on CPU and upload to GPU once
        var shiftsX = new int[shiftCount];
        var shiftsY = new int[shiftCount];
        for (var i = 0; i < shiftCount; i++)
        {
            shiftsX[i] = centerX + (i % stride) - radius;
            shiftsY[i] = centerY + (i / stride) - radius;
        }

        using var gpuShiftsX = this.context.Accelerator.Allocate1D<int>(shiftCount);
        using var gpuShiftsY = this.context.Accelerator.Allocate1D<int>(shiftCount);
        gpuShiftsX.CopyFromCPU(shiftsX);
        gpuShiftsY.CopyFromCPU(shiftsY);

        var totalThreads = shiftCount * ShiftScoreThreadsPerShift;
        using var partialResults = this.context.Accelerator.Allocate1D<ScorePartial>(totalThreads);

        for (var levelIndex = 0; levelIndex < levelCount; levelIndex++)
        {
            var refLevel = (AlignBitmapLevelGPU)referenceLevels[levelIndex];
            var candLevel = (AlignBitmapLevelGPU)rotatedLevels[levelIndex];
            if (refLevel.Width != candLevel.Width || refLevel.Height != candLevel.Height)
                continue;

            partialResults.MemSetToZero();
            this.shiftScoreKernel(
                totalThreads,
                refLevel.GpuGrayscale.View, refLevel.GpuValidityMask.View,
                candLevel.GpuGrayscale.View, candLevel.GpuValidityMask.View,
                refLevel.Width, refLevel.Height,
                gpuShiftsX.View, gpuShiftsY.View,
                shiftCount,
                partialResults.View);

            // Transfer only partial sums back (tiny: shiftCount * 256 ScorePartial)
            var partials = partialResults.GetAsArray1D();

            for (var shiftIdx = 0; shiftIdx < shiftCount; shiftIdx++)
            {
                double sumRef = 0, sumCand = 0, sumRef2 = 0, sumCand2 = 0, sumProd = 0;
                var valid = 0;
                var baseIdx = shiftIdx * ShiftScoreThreadsPerShift;
                for (var t = 0; t < ShiftScoreThreadsPerShift; t++)
                {
                    var p = partials[baseIdx + t];
                    sumRef += p.SumRef;
                    sumCand += p.SumCand;
                    sumRef2 += p.SumRef2;
                    sumCand2 += p.SumCand2;
                    sumProd += p.SumProd;
                    valid += p.Valid;
                }

                if (valid > 0)
                {
                    var corr = ComputeCorrelation(sumRef, sumCand, sumRef2, sumCand2, sumProd, valid);
                    if (corr > double.MinValue / 2)
                    {
                        totalScores[shiftIdx] += corr * valid;
                        totalValids[shiftIdx] += valid;
                    }
                }
            }
        }

        var bestScore = double.MinValue;
        var bestShiftX = centerX;
        var bestShiftY = centerY;
        var bestValid = 0L;

        for (var i = 0; i < shiftCount; i++)
        {
            if (totalValids[i] == 0) continue;
            var score = totalScores[i] / totalValids[i];
            if (score > bestScore)
            {
                bestScore = score;
                bestShiftX = centerX + (i % stride) - radius;
                bestShiftY = centerY + (i / stride) - radius;
                bestValid = totalValids[i];
            }
        }

        return bestValid == 0
            ? new AlignShiftScore(centerX, centerY, 0, 1)
            : new AlignShiftScore(bestShiftX, bestShiftY, (int)Math.Clamp((1d - bestScore) * 1_000_000d, 0d, int.MaxValue), (int)Math.Min(bestValid, int.MaxValue));
    }

    private static (double Correlation, int Valid) ScoreGrayscaleCorrelation(byte[] reference, byte[] referenceMask, byte[] candidate, byte[] candidateMask, int width, int height,
        int shiftX, int shiftY)
    {
        var startX = Math.Max(0, shiftX);
        var startY = Math.Max(0, shiftY);
        var candidateStartX = Math.Max(0, -shiftX);
        var candidateStartY = Math.Max(0, -shiftY);
        var overlapWidth = width - Math.Abs(shiftX);
        var overlapHeight = height - Math.Abs(shiftY);
        if (overlapWidth <= 0 || overlapHeight <= 0)
        {
            return (double.MinValue, 0);
        }

        var lockObj = new object();
        var sumReference = 0d;
        var sumCandidate = 0d;
        var sumReference2 = 0d;
        var sumCandidate2 = 0d;
        var sumProduct = 0d;
        var valid = 0;

        Parallel.For(0, overlapHeight, () => (SumRef: 0d, SumC: 0d, SumR2: 0d, SumC2: 0d, SumP: 0d, V: 0),
            (y, _, local) =>
            {
                var refRowBase = (startY + y) * width + startX;
                var candRowBase = (candidateStartY + y) * width + candidateStartX;
                for (var x = 0; x < overlapWidth; x++)
                {
                    var refIdx = refRowBase + x;
                    var candIdx = candRowBase + x;
                    if (referenceMask[refIdx] == 0 || candidateMask[candIdx] == 0) continue;
                    var rv = reference[refIdx];
                    var cv = candidate[candIdx];
                    local.SumRef += rv;
                    local.SumC += cv;
                    local.SumR2 += rv * rv;
                    local.SumC2 += cv * cv;
                    local.SumP += rv * cv;
                    local.V++;
                }
                return local;
            },
            local =>
            {
                lock (lockObj)
                {
                    sumReference += local.SumRef;
                    sumCandidate += local.SumC;
                    sumReference2 += local.SumR2;
                    sumCandidate2 += local.SumC2;
                    sumProduct += local.SumP;
                    valid += local.V;
                }
            });

        if (valid <= 1)
        {
            return (double.MinValue, 0);
        }

        var referenceVariance = sumReference2 - (sumReference * sumReference / valid);
        var candidateVariance = sumCandidate2 - (sumCandidate * sumCandidate / valid);
        var denominator = Math.Sqrt(referenceVariance * candidateVariance);
        if (denominator <= 1e-9)
        {
            return (double.MinValue, 0);
        }

        return ((sumProduct - (sumReference * sumCandidate / valid)) / denominator, valid);
    }

    private static (float X, float Y, double Score) RefineSubpixelShift(AlignBitmapLevelGPU referenceLevel, AlignBitmapLevelGPU candidateLevel, float centerX, float centerY)
    {
        var referenceGrayscale = referenceLevel.GpuGrayscale.GetAsArray1D();
        var referenceMask = referenceLevel.GpuValidityMask.GetAsArray1D();
        var candidateGrayscale = candidateLevel.GpuGrayscale.GetAsArray1D();
        var candidateMask = candidateLevel.GpuValidityMask.GetAsArray1D();

        const float radius = 1f;
        const float step = 0.25f;
        const int sampleStride = 8;

        var shifts = new List<(float X, float Y)>();
        for (var shiftY = centerY - radius; shiftY <= centerY + radius + 0.001f; shiftY += step)
        for (var shiftX = centerX - radius; shiftX <= centerX + radius + 0.001f; shiftX += step)
            shifts.Add((shiftX, shiftY));

        var scores = new double[shifts.Count];
        Parallel.For(0, shifts.Count, i =>
        {
            scores[i] = ScoreSubpixelCorrelation(referenceGrayscale, referenceMask, candidateGrayscale, candidateMask,
                referenceLevel.Width, referenceLevel.Height, shifts[i].X, shifts[i].Y, sampleStride);
        });

        var bestIdx = 0;
        for (var i = 1; i < scores.Length; i++)
        {
            if (scores[i] > scores[bestIdx]) bestIdx = i;
        }

        return (shifts[bestIdx].X, shifts[bestIdx].Y, scores[bestIdx]);
    }

    private static double ScoreSubpixelCorrelation(byte[] referenceGrayscale, byte[] referenceMask, byte[] candidateGrayscale, byte[] candidateMask, int width, int height, float shiftX,
        float shiftY, int sampleStride)
    {
        var lockObj = new object();
        var sumReference = 0d;
        var sumCandidate = 0d;
        var sumReference2 = 0d;
        var sumCandidate2 = 0d;
        var sumProduct = 0d;
        var validWeight = 0d;

        var yStart = sampleStride;
        var yEnd = height - sampleStride;
        var yCount = ((yEnd - yStart) + sampleStride - 1) / sampleStride;

        Parallel.For(0, yCount, () => (R: 0d, C: 0d, R2: 0d, C2: 0d, P: 0d, W: 0d),
            (yi, _, local) =>
            {
                var y = yStart + yi * sampleStride;
                var rowOffset = y * width;
                var candidateY = y - shiftY;
                if (candidateY < 0 || candidateY >= height - 1) return local;

                for (var x = sampleStride; x < width - sampleStride; x += sampleStride)
                {
                    var referenceIndex = rowOffset + x;
                    if (referenceMask[referenceIndex] == 0) continue;

                    var candidateX = x - shiftX;
                    if (candidateX < 0 || candidateX >= width - 1) continue;

                    var cw = SampleBilinear(candidateMask, width, candidateX, candidateY);
                    if (cw <= 0.001f) continue;

                    var rv = referenceGrayscale[referenceIndex];
                    var cv = SampleBilinear(candidateGrayscale, width, candidateX, candidateY);
                    local.R += rv * cw;
                    local.C += cv * cw;
                    local.R2 += rv * rv * cw;
                    local.C2 += cv * cv * cw;
                    local.P += rv * cv * cw;
                    local.W += cw;
                }
                return local;
            },
            local =>
            {
                lock (lockObj)
                {
                    sumReference += local.R;
                    sumCandidate += local.C;
                    sumReference2 += local.R2;
                    sumCandidate2 += local.C2;
                    sumProduct += local.P;
                    validWeight += local.W;
                }
            });

        if (validWeight <= 1e-6)
        {
            return double.MinValue;
        }

        var referenceVariance = sumReference2 - (sumReference * sumReference / validWeight);
        var candidateVariance = sumCandidate2 - (sumCandidate * sumCandidate / validWeight);
        var denominator = Math.Sqrt(referenceVariance * candidateVariance);
        if (denominator <= 1e-9)
        {
            return double.MinValue;
        }

        return (sumProduct - (sumReference * sumCandidate / validWeight)) / denominator;
    }

    private static float SampleBilinear(byte[] source, int width, float x, float y)
    {
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        var index00 = y0 * width + x0;
        var top = source[index00] * (1f - fx) + source[index00 + 1] * fx;
        var bottom = source[index00 + width] * (1f - fx) + source[index00 + width + 1] * fx;
        return top * (1f - fy) + bottom * fy;
    }

    protected override IReadOnlyList<IAlignBitmapLevel> BuildPyramid(IImageProxy image)
    {
        var levels = new List<IAlignBitmapLevel>();
        var width = image.Width;
        var height = image.Height;
        var pixelCount = width * height;

        var rgb = new byte[pixelCount * 3];
        image.LoadFullImage(rgb);
        using var rgbBuffer = this.context.Accelerator.Allocate1D<byte>(rgb.Length);
        var grayscaleBuffer = this.context.Accelerator.Allocate1D<byte>(pixelCount);
        var validityMaskBuffer = this.context.Accelerator.Allocate1D<byte>(pixelCount);
        rgbBuffer.CopyFromCPU(rgb);
        this.initKernel(pixelCount, rgbBuffer.View, grayscaleBuffer.View, validityMaskBuffer.View);

        while (true)
        {
            levels.Add(this.CreateLevel(width, height, grayscaleBuffer, validityMaskBuffer));

            if (width < 64 || height < 64)
            {
                break;
            }

            var resultWidth = Math.Max(1, width / 2);
            var resultHeight = Math.Max(1, height / 2);
            var resultGray = this.context.Accelerator.Allocate1D<byte>(resultWidth * resultHeight);
            var resultMask = this.context.Accelerator.Allocate1D<byte>(resultWidth * resultHeight);

            this.downsampleKernel((int)resultGray.Length, grayscaleBuffer.View, validityMaskBuffer.View, width, height, resultGray.View, resultMask.View, resultWidth, resultHeight);

            grayscaleBuffer = resultGray;
            validityMaskBuffer = resultMask;
            width = resultWidth;
            height = resultHeight;
        }

        return levels;
    }

    protected override IReadOnlyList<IAlignBitmapLevel> RotatePyramid(IReadOnlyList<IAlignBitmapLevel> levels, float angle)
    {
        var rotated = new List<IAlignBitmapLevel>(levels.Count);
        try
        {
            foreach (var level in levels)
            {
                var gpuLevel = (AlignBitmapLevelGPU)level;
                rotated.Add(this.RotateLevel(gpuLevel, angle));
            }
        }
        catch
        {
            foreach (var level in rotated)
            {
                level.Dispose();
            }

            throw;
        }

        return rotated;
    }

    protected override IAlignBitmapLevel RotateLevel(IAlignBitmapLevel level, float angle)
    {
        var gpuLevel = (AlignBitmapLevelGPU)level;
        var rotatedGrayGpu = this.context.Accelerator.Allocate1D<byte>(gpuLevel.GpuGrayscale.Length);
        var rotatedMaskGpu = this.context.Accelerator.Allocate1D<byte>(gpuLevel.GpuValidityMask.Length);
        this.rotateKernel((int)rotatedGrayGpu.Length, gpuLevel.GpuGrayscale.View, gpuLevel.GpuValidityMask.View, gpuLevel.Width, gpuLevel.Height, angle, rotatedGrayGpu.View,
            rotatedMaskGpu.View);

        return this.CreateLevel(gpuLevel.Width, gpuLevel.Height, rotatedGrayGpu, rotatedMaskGpu);
    }

    private AlignBitmapLevelGPU CreateLevel(int width, int height, MemoryBuffer1D<byte, Stride1D.Dense> grayscaleBuffer,
        MemoryBuffer1D<byte, Stride1D.Dense> validityMaskBuffer)
    {
        var pixelCount = width * height;
        var histogramBuffer = this.pooledHistogram ??= this.context.Accelerator.Allocate1D<int>(256);
        histogramBuffer.MemSetToZero();
        this.histogramKernel(pixelCount, grayscaleBuffer.View, validityMaskBuffer.View, histogramBuffer.View);
        var histogram = histogramBuffer.GetAsArray1D();
        var median = ComputeMedianFromHistogram(histogram);
        var bitmapBuffer = this.context.Accelerator.Allocate1D<byte>(pixelCount);
        var maskBuffer = this.context.Accelerator.Allocate1D<byte>(pixelCount);
        this.thresholdKernel(pixelCount, grayscaleBuffer.View, validityMaskBuffer.View, median, bitmapBuffer.View, maskBuffer.View);
        return new AlignBitmapLevelGPU(width, height, median, grayscaleBuffer, validityMaskBuffer, bitmapBuffer, maskBuffer);
    }

    private static byte ComputeMedianFromHistogram(int[] histogram)
    {
        var validCount = 0;
        for (var i = 0; i < 256; i++)
            validCount += histogram[i];

        if (validCount == 0) return 0;

        var midpoint = (validCount - 1) / 2;
        var cumulative = 0;
        for (var value = 0; value < 256; value++)
        {
            cumulative += histogram[value];
            if (cumulative > midpoint)
                return (byte)value;
        }

        return 255;
    }

    private static void InitGrayscaleAndMaskKernel(Index1D index, ArrayView<byte> sourceRgb, ArrayView<byte> destinationGray, ArrayView<byte> validityMask)
    {
        var sourceOffset = index * 3;
        destinationGray[index] = (byte)((54 * sourceRgb[sourceOffset] + 183 * sourceRgb[sourceOffset + 1] + 19 * sourceRgb[sourceOffset + 2]) >> 8);
        validityMask[index] = 1;
    }

    private static void DownsampleKernel(Index1D index, ArrayView<byte> source, ArrayView<byte> validityMask, int width, int height, ArrayView<byte> destination,
        ArrayView<byte> destinationMask, int resultWidth, int resultHeight)
    {
        var y = index / resultWidth;
        var x = index - y * resultWidth;
        if (x >= resultWidth || y >= resultHeight)
        {
            return;
        }

        var sourceY = y * 2;
        var sourceX = x * 2;
        var sum = 0;
        var count = 0;

        var sx0 = sourceX;
        var sy0 = sourceY;
        if (sx0 >= 0 && sy0 >= 0 && sx0 < width && sy0 < height)
        {
            var sourceIndex = sy0 * width + sx0;
            if (validityMask[sourceIndex] != 0)
            {
                sum += source[sourceIndex];
                count++;
            }
        }

        var sx1 = sourceX + 1;
        var sy1 = sourceY;
        if (sx1 >= 0 && sy1 >= 0 && sx1 < width && sy1 < height)
        {
            var sourceIndex = sy1 * width + sx1;
            if (validityMask[sourceIndex] != 0)
            {
                sum += source[sourceIndex];
                count++;
            }
        }

        var sx2 = sourceX;
        var sy2 = sourceY + 1;
        if (sx2 >= 0 && sy2 >= 0 && sx2 < width && sy2 < height)
        {
            var sourceIndex = sy2 * width + sx2;
            if (validityMask[sourceIndex] != 0)
            {
                sum += source[sourceIndex];
                count++;
            }
        }

        var sx3 = sourceX + 1;
        var sy3 = sourceY + 1;
        if (sx3 >= 0 && sy3 >= 0 && sx3 < width && sy3 < height)
        {
            var sourceIndex = sy3 * width + sx3;
            if (validityMask[sourceIndex] != 0)
            {
                sum += source[sourceIndex];
                count++;
            }
        }

        if (count > 0)
        {
            destination[index] = (byte)(sum / count);
            destinationMask[index] = 1;
            return;
        }

        destination[index] = 0;
        destinationMask[index] = 0;
    }

    private static void RotateKernel(Index1D index, ArrayView<byte> source, ArrayView<byte> validityMask, int width, int height, float angle, ArrayView<byte> destination,
        ArrayView<byte> destinationMask)
    {
        var y = index / width;
        var x = index - y * width;
        if (x >= width || y >= height)
        {
            return;
        }

        var radians = -angle * XMath.PI / 180f;
        var cos = XMath.Cos(radians);
        var sin = XMath.Sin(radians);
        var centerX = (width - 1) * 0.5f;
        var centerY = (height - 1) * 0.5f;
        var dx = x - centerX;
        var dy = y - centerY;
        var srcX = cos * dx - sin * dy + centerX;
        var srcY = sin * dx + cos * dy + centerY;
        var sampleX = GpuHelper.RoundToInt(srcX);
        var sampleY = GpuHelper.RoundToInt(srcY);
        if (sampleX < 0 || sampleY < 0 || sampleX >= width || sampleY >= height)
        {
            destination[index] = 0;
            destinationMask[index] = 0;
            return;
        }

        var sourceIndex = sampleY * width + sampleX;
        if (validityMask[sourceIndex] == 0)
        {
            destination[index] = 0;
            destinationMask[index] = 0;
            return;
        }

        destination[index] = source[sourceIndex];
        destinationMask[index] = 1;
    }

    private static void BuildBitmapAndMaskKernel(Index1D index, ArrayView<byte> grayscale, ArrayView<byte> validityMask, byte medianThreshold, ArrayView<byte> bitmap,
        ArrayView<byte> mask)
    {
        var value = grayscale[index];
        bitmap[index] = (byte)(value >= medianThreshold ? 1 : 0);
        mask[index] = (byte)(validityMask[index] != 0 && XMath.Abs(value - medianThreshold) > ExclusionWindow ? 1 : 0);
    }

    private static void HistogramKernel(Index1D index, ArrayView<byte> grayscale, ArrayView<byte> validityMask, ArrayView<int> histogram)
    {
        if (validityMask[index] != 0)
        {
            var histIdx = new Index1D(grayscale[index]);
            Atomic.Add(ref histogram[histIdx], 1);
        }
    }

    private static void ScoreShiftPartialKernel(
        Index1D index,
        ArrayView<byte> reference, ArrayView<byte> refMask,
        ArrayView<byte> candidate, ArrayView<byte> candMask,
        int width, int height,
        ArrayView<int> shiftsX, ArrayView<int> shiftsY,
        int shiftCount,
        ArrayView<ScorePartial> results)
    {
        var globalIdx = (int)index;
        var shiftIdx = globalIdx / ShiftScoreThreadsPerShift;
        var threadInShift = globalIdx % ShiftScoreThreadsPerShift;
        if (shiftIdx >= shiftCount) return;

        var shiftX = shiftsX[shiftIdx];
        var shiftY = shiftsY[shiftIdx];

        var startX = XMath.Max(0, shiftX);
        var startY = XMath.Max(0, shiftY);
        var candStartX = XMath.Max(0, -shiftX);
        var candStartY = XMath.Max(0, -shiftY);
        var overlapW = width - XMath.Abs(shiftX);
        var overlapH = height - XMath.Abs(shiftY);
        if (overlapW <= 0 || overlapH <= 0) return;

        double sumRef = 0, sumCand = 0, sumRef2 = 0, sumCand2 = 0, sumProd = 0;
        var valid = 0;
        var totalPixels = overlapW * overlapH;

        for (var pixelIdx = threadInShift; pixelIdx < totalPixels; pixelIdx += ShiftScoreThreadsPerShift)
        {
            var y = pixelIdx / overlapW;
            var x = pixelIdx - y * overlapW;
            var refIdx = (startY + y) * width + startX + x;
            var candIdx = (candStartY + y) * width + candStartX + x;
            if (refMask[refIdx] == 0 || candMask[candIdx] == 0) continue;

            var rv = reference[refIdx];
            var cv = candidate[candIdx];
            sumRef += rv;
            sumCand += cv;
            sumRef2 += rv * rv;
            sumCand2 += cv * cv;
            sumProd += rv * cv;
            valid++;
        }

        var resultIdx = shiftIdx * ShiftScoreThreadsPerShift + threadInShift;
        var result = results[resultIdx];
        result.SumRef = sumRef;
        result.SumCand = sumCand;
        result.SumRef2 = sumRef2;
        result.SumCand2 = sumCand2;
        result.SumProd = sumProd;
        result.Valid = valid;
        results[resultIdx] = result;
    }

    private static double ComputeCorrelation(double sumRef, double sumCand, double sumRef2, double sumCand2, double sumProd, int valid)
    {
        if (valid <= 1) return double.MinValue;
        var refVar = sumRef2 - sumRef * sumRef / valid;
        var candVar = sumCand2 - sumCand * sumCand / valid;
        var denom = Math.Sqrt(refVar * candVar);
        if (denom <= 1e-9) return double.MinValue;
        return (sumProd - sumRef * sumCand / valid) / denom;
    }
}

internal sealed class GpuGrayscaleData : ImageAlignerPyramid.IGrayscaleData
{
    private MemoryBuffer1D<byte, Stride1D.Dense>? grayscaleBuffer;

    public GpuGrayscaleData(MemoryBuffer1D<byte, Stride1D.Dense> grayscaleBuffer)
    {
        this.grayscaleBuffer = grayscaleBuffer;
    }

    public byte[] GetBytes()
    {
        if (this.grayscaleBuffer == null)
        {
            throw new ObjectDisposedException(nameof(GpuGrayscaleData));
        }

        return this.grayscaleBuffer.GetAsArray1D();
    }

    public MemoryBuffer1D<byte, Stride1D.Dense> MoveBuffer()
    {
        if (this.grayscaleBuffer == null)
        {
            throw new ObjectDisposedException(nameof(GpuGrayscaleData));
        }

        var buffer = this.grayscaleBuffer;
        this.grayscaleBuffer = null;
        return buffer;
    }

    public void Dispose()
    {
        this.grayscaleBuffer?.Dispose();
        this.grayscaleBuffer = null;
    }
}
