// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Align;

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

internal static class AlignShiftScorer
{
    internal readonly record struct ScorePlane(int Width, int Height, byte[] Bitmap, byte[] Mask);

    public static AlignShiftScore FindBestShift(IReadOnlyList<ScorePlane> referencePlanes, IReadOnlyList<ScorePlane> candidatePlanes, int centerX, int centerY, int radius)
    {
        var planeCount = Math.Min(referencePlanes.Count, candidatePlanes.Count);
        if (planeCount == 0)
        {
            return new AlignShiftScore(centerX, centerY, 0, 1);
        }

        var best = new AlignShiftScore(centerX, centerY, int.MaxValue, 0);
        for (var shiftY = centerY - radius; shiftY <= centerY + radius; shiftY++)
        {
            for (var shiftX = centerX - radius; shiftX <= centerX + radius; shiftX++)
            {
                var mismatch = 0;
                var valid = 0;
                for (var levelIndex = 0; levelIndex < planeCount; levelIndex++)
                {
                    var referencePlane = referencePlanes[levelIndex];
                    var candidatePlane = candidatePlanes[levelIndex];
                    if (referencePlane.Width != candidatePlane.Width || referencePlane.Height != candidatePlane.Height)
                    {
                        continue;
                    }

                    var current = Score(referencePlane, candidatePlane, shiftX, shiftY);
                    mismatch += current.Mismatch;
                    valid += current.Valid;
                }

                var score = new AlignShiftScore(shiftX, shiftY, mismatch, valid);
                if (score.ValidCount > 0 && score.NormalizedScore < best.NormalizedScore)
                {
                    best = score;
                }
            }
        }

        return best.ValidCount == 0 ? new AlignShiftScore(centerX, centerY, 0, 1) : best;
    }

    private static (int Mismatch, int Valid) Score(ScorePlane referencePlane, ScorePlane candidatePlane, int shiftX, int shiftY)
    {
        var startX = Math.Max(0, shiftX);
        var startY = Math.Max(0, shiftY);
        var targetX = Math.Max(0, -shiftX);
        var targetY = Math.Max(0, -shiftY);
        var width = Math.Min(referencePlane.Width - startX, candidatePlane.Width - targetX);
        var height = Math.Min(referencePlane.Height - startY, candidatePlane.Height - targetY);
        if (width <= 0 || height <= 0)
        {
            return (int.MaxValue, 0);
        }

        return SystemHelper.UseAvx && Avx2.IsSupported
            ? ScoreSimd(referencePlane, candidatePlane, startX, startY, targetX, targetY, width, height)
            : ScoreClassic(referencePlane, candidatePlane, startX, startY, targetX, targetY, width, height);
    }

    private static (int Mismatch, int Valid) ScoreClassic(ScorePlane referencePlane, ScorePlane candidatePlane, int startX, int startY, int targetX, int targetY, int width,
        int height)
    {
        var mismatch = 0;
        var valid = 0;
        for (var y = 0; y < height; y++)
        {
            var referenceOffset = (startY + y) * referencePlane.Width + startX;
            var candidateOffset = (targetY + y) * candidatePlane.Width + targetX;
            for (var x = 0; x < width; x++)
            {
                var validPixel = referencePlane.Mask[referenceOffset + x] & candidatePlane.Mask[candidateOffset + x];
                valid += validPixel;
                mismatch += (referencePlane.Bitmap[referenceOffset + x] ^ candidatePlane.Bitmap[candidateOffset + x]) & validPixel;
            }
        }

        return (mismatch, valid);
    }

    private static unsafe (int Mismatch, int Valid) ScoreSimd(ScorePlane referencePlane, ScorePlane candidatePlane, int startX, int startY, int targetX, int targetY, int width,
        int height)
    {
        var mismatch = 0;
        var valid = 0;
        var zero = Vector256<byte>.Zero;
        var accumulator = stackalloc ulong[4];

        fixed (byte* refBitmap = referencePlane.Bitmap, refMask = referencePlane.Mask, candBitmap = candidatePlane.Bitmap, candMask = candidatePlane.Mask)
        {
            for (var y = 0; y < height; y++)
            {
                var referenceOffset = (startY + y) * referencePlane.Width + startX;
                var candidateOffset = (targetY + y) * candidatePlane.Width + targetX;
                var x = 0;
                for (; x <= width - Vector256<byte>.Count; x += Vector256<byte>.Count)
                {
                    var refBits = Avx.LoadVector256(refBitmap + referenceOffset + x);
                    var refMasks = Avx.LoadVector256(refMask + referenceOffset + x);
                    var candBits = Avx.LoadVector256(candBitmap + candidateOffset + x);
                    var candMasks = Avx.LoadVector256(candMask + candidateOffset + x);
                    var validMask = Avx2.And(refMasks, candMasks);
                    var mismatchMask = Avx2.And(Avx2.Xor(refBits, candBits), validMask);

                    var validSums = Avx2.SumAbsoluteDifferences(validMask, zero);
                    var mismatchSums = Avx2.SumAbsoluteDifferences(mismatchMask, zero);
                    Avx.Store(accumulator, validSums.AsUInt64());
                    valid += (int)(accumulator[0] + accumulator[1] + accumulator[2] + accumulator[3]);
                    Avx.Store(accumulator, mismatchSums.AsUInt64());
                    mismatch += (int)(accumulator[0] + accumulator[1] + accumulator[2] + accumulator[3]);
                }

                for (; x < width; x++)
                {
                    var validPixel = referencePlane.Mask[referenceOffset + x] & candidatePlane.Mask[candidateOffset + x];
                    valid += validPixel;
                    mismatch += (referencePlane.Bitmap[referenceOffset + x] ^ candidatePlane.Bitmap[candidateOffset + x]) & validPixel;
                }
            }
        }

        return (mismatch, valid);
    }
}
