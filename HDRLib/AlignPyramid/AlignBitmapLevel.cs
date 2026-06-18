// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Align;

using System.Runtime.CompilerServices;

internal class AlignBitmapLevel : ImageAlignerPyramid.IAlignBitmapLevel
{
    public AlignBitmapLevel(int width, int height, byte[] grayscale, byte[]? validityMask = null)
    {
        this.Width = width;
        this.Height = height;
        this.Grayscale = grayscale;
        this.Bitmap = new byte[grayscale.Length];
        this.Mask = new byte[grayscale.Length];

        this.ValidityMask = validityMask ?? CreateFullyValidMask(grayscale.Length);
        this.MedianThreshold = ComputeMedianThreshold(grayscale, this.ValidityMask);
        const int exclusionWindow = 4;
        for (var i = 0; i < grayscale.Length; i++)
        {
            var value = grayscale[i];
            this.Bitmap[i] = (byte)(value >= this.MedianThreshold ? 1 : 0);
            this.Mask[i] = (byte)(this.ValidityMask[i] != 0 && Math.Abs(value - this.MedianThreshold) > exclusionWindow ? 1 : 0);
        }
    }

    public int Width { get; }

    public int Height { get; }

    public byte MedianThreshold { get; }

    public byte[] Grayscale { get; }

    public byte[] Bitmap { get; }

    public byte[] Mask { get; }

    public byte[] ValidityMask { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetIndex(int x, int y) => y * this.Width + x;

    public virtual void Dispose() { }

    private static byte[] CreateFullyValidMask(int length)
    {
        var mask = new byte[length];
        mask.AsSpan().Fill(1);
        return mask;
    }

    internal static byte ComputeMedianThreshold(byte[] grayscale, byte[] validityMask)
    {
        Span<int> histogram = stackalloc int[256];
        var validCount = 0;
        for (var i = 0; i < grayscale.Length; i++)
        {
            if (validityMask[i] == 0)
            {
                continue;
            }

            histogram[grayscale[i]]++;
            validCount++;
        }

        if (validCount == 0)
        {
            return 0;
        }

        var midpoint = (validCount - 1) / 2;
        var cumulative = 0;
        for (var value = 0; value < histogram.Length; value++)
        {
            cumulative += histogram[value];
            if (cumulative > midpoint)
            {
                return (byte)value;
            }
        }

        return 255;
    }
}
