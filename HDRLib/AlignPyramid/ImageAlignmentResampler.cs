// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Align;

using Interfaces;

internal static class ImageAlignmentResampler
{
    public static IImageProxy Apply(IImageProxy source, AlignmentTransform transform)
    {
        if (Math.Abs(transform.Angle) < 0.001f && transform.X == 0 && transform.Y == 0)
        {
            return source.Clone();
        }

        var sourceRows = new byte[source.Height][];
        for (var y = 0; y < source.Height; y++)
        {
            sourceRows[y] = source.LoadRow(y);
        }

        var result = source.Clone();
        var centerX = (source.Width - 1) * 0.5f;
        var centerY = (source.Height - 1) * 0.5f;
        var radians = -transform.Angle * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);

        Parallel.For(0, source.Height, y =>
        {
            var row = new byte[source.Width * 3];
            for (var x = 0; x < source.Width; x++)
            {
                var dx = x - centerX - transform.X;
                var dy = y - centerY - transform.Y;
                var srcX = cos * dx - sin * dy + centerX;
                var srcY = sin * dx + cos * dy + centerY;
                SampleBilinear(sourceRows, source.Width, source.Height, srcX, srcY, row, x * 3);
            }

            result.SaveRow(y, row);
        });

        return result;
    }

    private static void SampleBilinear(byte[][] sourceRows, int width, int height, float x, float y, byte[] destination, int offset)
    {
        if (x < 0 || y < 0 || x >= width - 1 || y >= height - 1)
        {
            destination[offset] = 0;
            destination[offset + 1] = 0;
            destination[offset + 2] = 0;
            return;
        }

        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var x1 = x0 + 1;
        var y1 = y0 + 1;
        var fx = x - x0;
        var fy = y - y0;
        var row0 = sourceRows[y0];
        var row1 = sourceRows[y1];
        var offset00 = x0 * 3;
        var offset10 = x1 * 3;

        for (var channel = 0; channel < 3; channel++)
        {
            var top = row0[offset00 + channel] * (1f - fx) + row0[offset10 + channel] * fx;
            var bottom = row1[offset00 + channel] * (1f - fx) + row1[offset10 + channel] * fx;
            destination[offset + channel] = (byte)Math.Clamp((int)MathF.Round(top * (1f - fy) + bottom * fy), 0, 255);
        }
    }
}
