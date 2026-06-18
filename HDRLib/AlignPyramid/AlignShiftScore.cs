// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Align;

public readonly record struct AlignShiftScore(int X, int Y, int MismatchCount, int ValidCount)
{
    public double NormalizedScore => this.ValidCount == 0 ? double.MaxValue : (double)this.MismatchCount / this.ValidCount;
}
