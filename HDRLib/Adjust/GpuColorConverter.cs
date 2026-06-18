// Copyright (c) Stanislav Popov. All rights reserved.

using HDRLib.Adjust;
using HDRLib.Gpu;
using HDRLib.Image;
using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;

public class GpuColorConverter
{
    #region Fields

    private readonly Accelerator accelerator;
    private readonly Action<Index2D, ArrayView1D<Lab, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>, int, int> fromLabKernel;

    private readonly Action<Index2D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Lab, Stride1D.Dense>, int, int> toLabKernel;

    #endregion

    #region Constructors

    public GpuColorConverter(GpuContext gpuContext)
    {
        this.accelerator = gpuContext.Accelerator;

        this.toLabKernel =
            this.accelerator
                .LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Lab, Stride1D.Dense>, int, int>(ToLabKernel);

        this.fromLabKernel = this.fromLabKernel =
            this.accelerator.LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<Lab, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>, int, int>(
                FromLabKernel);
    }

    #endregion

    #region Methods

    public ArrayView1D<Lab, Stride1D.Dense> ToLab(ArrayView1D<Rgb, Stride1D.Dense> input, int width, int height)
    {
        var extent = new Index2D(height, width);
        var outputBuffer = this.accelerator.Allocate1D<Lab>(input.Extent.Size);
        this.toLabKernel(extent, input, outputBuffer.View, height, width);
        this.accelerator.Synchronize();
        return outputBuffer.View;
    }

    public ArrayView1D<Rgb, Stride1D.Dense> FromLab(ArrayView1D<Lab, Stride1D.Dense> input, int width, int height)
    {
        var extent = new Index2D(height, width);
        var outputBuffer = this.accelerator.Allocate1D<Rgb>(input.Extent.Size);
        this.fromLabKernel(extent, input, outputBuffer, height, width);
        this.accelerator.Synchronize();
        return outputBuffer.View;
    }

    public double[][][] ToHsl(double[][][] rgb)
    {
        if (rgb == null)
        {
            throw new ArgumentNullException(nameof(rgb));
        }

        var channels = rgb.Length;
        if (channels != 3)
        {
            throw new ArgumentException("rgb must have 3 channels (R,G,B)");
        }

        var h = rgb[0].Length;
        var w = rgb[0][0].Length;

        var flat = new double[h * w * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var b = (y * w + x) * 3;
            flat[b + 0] = rgb[0][y][x];
            flat[b + 1] = rgb[1][y][x];
            flat[b + 2] = rgb[2][y][x];
        }

        using var devIn = this.accelerator.Allocate1D(flat);
        using var devOut = this.accelerator.Allocate1D<double>(flat.Length);

        var extent = new Index2D(h, w);
        this.accelerator
            .LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<double, Stride1D.Dense>, ArrayView1D<double, Stride1D.Dense>, int, int>(ToHslKernel)(
                extent, devIn.View, devOut.View, h, w);
        this.accelerator.Synchronize();

        var flatOut = devOut.GetAsArray1D();
        var hsl = new double[3][][];
        for (var c = 0; c < 3; c++)
        {
            hsl[c] = new double[h][];
            for (var y = 0; y < h; y++)
            {
                hsl[c][y] = new double[w];
                for (var x = 0; x < w; x++)
                {
                    var b = (y * w + x) * 3;
                    hsl[c][y][x] = flatOut[b + c];
                }
            }
        }

        return hsl;
    }

    public double[][][] FromHsl(double[][][] hsl)
    {
        if (hsl == null)
        {
            throw new ArgumentNullException(nameof(hsl));
        }

        var channels = hsl.Length;
        if (channels != 3)
        {
            throw new ArgumentException("hsl must have 3 channels (H,S,L)");
        }

        var h = hsl[0].Length;
        var w = hsl[0][0].Length;

        var flat = new double[h * w * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var b = (y * w + x) * 3;
            flat[b + 0] = hsl[0][y][x];
            flat[b + 1] = hsl[1][y][x];
            flat[b + 2] = hsl[2][y][x];
        }

        using var devIn = this.accelerator.Allocate1D(flat);
        using var devOut = this.accelerator.Allocate1D<double>(flat.Length);

        var extent = new Index2D(h, w);
        this.accelerator
            .LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<double, Stride1D.Dense>, ArrayView1D<double, Stride1D.Dense>, int, int>(FromHslKernel)(
                extent, devIn.View, devOut.View, h, w);
        this.accelerator.Synchronize();

        var flatOut = devOut.GetAsArray1D();
        var rgb = new double[3][][];
        for (var c = 0; c < 3; c++)
        {
            rgb[c] = new double[h][];
            for (var y = 0; y < h; y++)
            {
                rgb[c][y] = new double[w];
                for (var x = 0; x < w; x++)
                {
                    var b = (y * w + x) * 3;
                    rgb[c][y][x] = flatOut[b + c];
                }
            }
        }

        return rgb;
    }

    private static void ToLabKernel(Index2D idx, ArrayView1D<Rgb, Stride1D.Dense> input, ArrayView1D<Lab, Stride1D.Dense> output, int height,
        int width)
    {
        var y = idx.X;
        var x = idx.Y;
        if (y >= height || x >= width)
        {
            return;
        }

        var baseIdx = y * width + x;

        var r = (float)input[baseIdx].Red / 255.0f;
        var g = (float)input[baseIdx].Green / 255.0f;
        var b = (float)input[baseIdx].Blue / 255.0f;

        // sRGB -> linear
        r = r > 0.04045 ? GpuHelper.Pow((r + 0.055f) / 1.055f, 2.4f) : r / 12.92f;
        g = g > 0.04045 ? GpuHelper.Pow((g + 0.055f) / 1.055f, 2.4f) : g / 12.92f;
        b = b > 0.04045 ? GpuHelper.Pow((b + 0.055f) / 1.055f, 2.4f) : b / 12.92f;

        // RGB -> XYZ (D65)
        var X = r * 0.4124564f + g * 0.3575761f + b * 0.1804375f;
        var Y = r * 0.2126729f + g * 0.7151522f + b * 0.0721750f;
        var Z = r * 0.0193339f + g * 0.1191920f + b * 0.9503041f;

        // Normalize by D65
        var xr = X / 0.95047f;
        var yr = Y / 1.00000f;
        var zr = Z / 1.08883f;

        var fx = xr > 0.008856f ? GpuHelper.Pow(xr, 1.0f / 3.0f) : 7.787f * xr + 16.0f / 116.0f;
        var fy = yr > 0.008856f ? GpuHelper.Pow(yr, 1.0f / 3.0f) : 7.787f * yr + 16.0f / 116.0f;
        var fz = zr > 0.008856f ? GpuHelper.Pow(zr, 1.0f / 3.0f) : 7.787f * zr + 16.0f / 116.0f;

        var L = 116.0f * fy - 16.0f;
        var A = 500.0f * (fx - fy);
        var B = 200.0f * (fy - fz);

        output[baseIdx] = new Lab(L, A, B);
    }

    private static void FromLabKernel(Index2D idx, ArrayView1D<Lab, Stride1D.Dense> input, ArrayView1D<Rgb, Stride1D.Dense> output, int height,
        int width)
    {
        var y = idx.X;
        var x = idx.Y;
        if (y >= height || x >= width)
        {
            return;
        }

        var baseIdx = y * width + x;
        var pixel = input[baseIdx];
        float L = pixel.L;
        float A = pixel.A;
        float B = pixel.B;

        var fy = (L + 16.0f) / 116.0f;
        var fx = fy + A / 500.0f;
        var fz = fy - B / 200.0f;

        var xr = GpuHelper.Pow(fx, 3) > 0.008856f ? GpuHelper.Pow(fx, 3) : (fx - 16.0f / 116.0f) / 7.787f;
        var yr = L > 903.3 * 0.008856 ? GpuHelper.Pow(fy, 3) : L / 903.3f;
        var zr = GpuHelper.Pow(fz, 3) > 0.008856 ? GpuHelper.Pow(fz, 3) : (fz - 16.0f / 116.0f) / 7.787f;

        var X = xr * 0.95047f;
        var Y = yr * 1.0f;
        var Z = zr * 1.08883f;

        // XYZ -> linear RGB
        var r = X * 3.2404542f + Y * -1.5371385f + Z * -0.4985314f;
        var g = X * -0.9692660f + Y * 1.8760108f + Z * 0.0415560f;
        var b = X * 0.0556434f + Y * -0.2040259f + Z * 1.0572252f;

        // linear -> sRGB
        r = r > 0.0031308f ? 1.055f * GpuHelper.Pow(r, 1.0f / 2.4f) - 0.055f : 12.92f * r;
        g = g > 0.0031308f ? 1.055f * GpuHelper.Pow(g, 1.0f / 2.4f) - 0.055f : 12.92f * g;
        b = b > 0.0031308f ? 1.055f * GpuHelper.Pow(b, 1.0f / 2.4f) - 0.055f : 12.92f * b;

        output[baseIdx] = new Rgb(r * 255.0f, g * 255.0f, b * 255.0f);
    }

    private static void ToHslKernel(Index2D idx, ArrayView1D<double, Stride1D.Dense> input, ArrayView1D<double, Stride1D.Dense> output, int height,
        int width)
    {
        var y = idx.X;
        var x = idx.Y;
        if (y >= height || x >= width)
        {
            return;
        }

        var baseIdx = (y * width + x) * 3;

        var r = input[baseIdx + 0] / 255.0;
        var g = input[baseIdx + 1] / 255.0;
        var b = input[baseIdx + 2] / 255.0;

        var max = XMath.Max(r, XMath.Max(g, b));
        var min = XMath.Min(r, XMath.Min(g, b));
        var delta = max - min;

        var L = (max + min) / 2.0;
        var S = 0.0;
        var H = 0.0;

        if (delta > 1e-8)
        {
            S = L < 0.5 ? delta / (max + min) : delta / (2.0 - max - min);
            if (XMath.Abs(max - r) < 1e-8)
            {
                H = (g - b) / delta + (g < b ? 6.0 : 0.0);
            }
            else if (XMath.Abs(max - g) < 1e-8)
            {
                H = (b - r) / delta + 2.0;
            }
            else
            {
                H = (r - g) / delta + 4.0;
            }

            H *= 60.0; // degrees
        }

        output[baseIdx + 0] = H;
        output[baseIdx + 1] = S;
        output[baseIdx + 2] = L;
    }

    private static void FromHslKernel(Index2D idx, ArrayView1D<double, Stride1D.Dense> input, ArrayView1D<double, Stride1D.Dense> output, int height,
        int width)
    {
        var y = idx.X;
        var x = idx.Y;
        if (y >= height || x >= width)
        {
            return;
        }

        var baseIdx = (y * width + x) * 3;

        var H = input[baseIdx + 0];
        var S = input[baseIdx + 1];
        var L = input[baseIdx + 2];

        var C = (1.0 - XMath.Abs(2.0 * L - 1.0)) * S;

        // Avoid the modulo operator here since it can generate the Rem intrinsic on some backends.
        var hPrime = H / 60.0;
        var sector = (int)hPrime;
        var fractional = hPrime - sector;
        var X = C * (1.0 - XMath.Abs(2.0 * fractional - 1.0));

        var m = L - C / 2.0;

        double r1 = 0, g1 = 0, b1 = 0;
        if (H < 60)
        {
            r1 = C;
            g1 = X;
            b1 = 0;
        }
        else if (H < 120)
        {
            r1 = X;
            g1 = C;
            b1 = 0;
        }
        else if (H < 180)
        {
            r1 = 0;
            g1 = C;
            b1 = X;
        }
        else if (H < 240)
        {
            r1 = 0;
            g1 = X;
            b1 = C;
        }
        else if (H < 300)
        {
            r1 = X;
            g1 = 0;
            b1 = C;
        }
        else
        {
            r1 = C;
            g1 = 0;
            b1 = X;
        }

        var r = (r1 + m) * 255.0;
        var g = (g1 + m) * 255.0;
        var b = (b1 + m) * 255.0;

        // clamp
        output[baseIdx + 0] = XMath.Max(0, XMath.Min(255, r));
        output[baseIdx + 1] = XMath.Max(0, XMath.Min(255, g));
        output[baseIdx + 2] = XMath.Max(0, XMath.Min(255, b));
    }

    #endregion
}