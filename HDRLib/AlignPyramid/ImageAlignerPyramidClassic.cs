// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Align;

using Interfaces;

internal sealed class ImageAlignerPyramidClassic : ImageAlignerPyramid
{
    protected override bool ProcessCandidatesInParallel => true;

    public ImageAlignerPyramidClassic()
    {
    }

    protected override AlignShiftScore FindBestShift(IAlignBitmapLevel referenceLevel, IAlignBitmapLevel candidateLevel, int centerX, int centerY, int radius)
    {
        var referenceCpu = (AlignBitmapLevel)referenceLevel;
        var candidateCpu = (AlignBitmapLevel)candidateLevel;
        return AlignShiftScorer.FindBestShift(
            [new AlignShiftScorer.ScorePlane(referenceCpu.Width, referenceCpu.Height, referenceCpu.Bitmap, referenceCpu.Mask)],
            [new AlignShiftScorer.ScorePlane(candidateCpu.Width, candidateCpu.Height, candidateCpu.Bitmap, candidateCpu.Mask)],
            centerX,
            centerY,
            radius);
    }
}
