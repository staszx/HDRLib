// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Tests;

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Align;
using HDRLib.Hdr;
using HDRLib.Interfaces;
using HDRLib.MathUtils;
using NUnit.Framework;

[TestFixture]
public class PerformanceOptimizationTests
{
    private const float LogTolerance = 2e-4f;
    private const float ExpTolerance = 5e-4f;
    private const float PowTolerance = 5e-3f;

    [Test]
    public void AvxMath_LnFloat_MatchesMathF()
    {
        RequireAvx2();

        var inputs = new[] { 0.001f, 0.01f, 0.1f, 0.5f, 1f, 2f, 10f, 100f };
        var result = AvxMath.Ln(Vector256.Create(inputs[0], inputs[1], inputs[2], inputs[3], inputs[4], inputs[5], inputs[6], inputs[7]));

        Span<float> lanes = stackalloc float[8];
        result.CopyTo(lanes);

        for (var i = 0; i < lanes.Length; i++)
        {
            Assert.That(lanes[i], Is.EqualTo(MathF.Log(inputs[i])).Within(LogTolerance), $"input={inputs[i]}");
        }
    }

    [Test]
    public void AvxMath_ExpFloat_MatchesMathF()
    {
        RequireAvx2();

        var inputs = new[] { -5f, -2f, -1f, -0.5f, 0f, 0.5f, 1f, 2f };
        var result = AvxMath.Exp(Vector256.Create(inputs[0], inputs[1], inputs[2], inputs[3], inputs[4], inputs[5], inputs[6], inputs[7]));

        Span<float> lanes = stackalloc float[8];
        result.CopyTo(lanes);

        for (var i = 0; i < lanes.Length; i++)
        {
            Assert.That(lanes[i], Is.EqualTo(MathF.Exp(inputs[i])).Within(ExpTolerance), $"input={inputs[i]}");
        }
    }

    [Test]
    public void AvxMath_PowFloat_MatchesMathF()
    {
        RequireAvx2();

        var bases = Vector256.Create(0.1f, 0.5f, 1f, 2f, 10f, 0.25f, 0.8f, 3f);
        var exponents = Vector256.Create(2f, 3f, 1f, 0.5f, -1f, 0.3f, 4f, 0.333f);
        var result = AvxMath.Pow(bases, exponents);

        Span<float> lanes = stackalloc float[8];
        result.CopyTo(lanes);

        for (var i = 0; i < lanes.Length; i++)
        {
            Assert.That(lanes[i], Is.EqualTo(MathF.Pow(bases[i], exponents[i])).Within(PowTolerance), $"{bases[i]}^{exponents[i]}");
        }
    }

    [Test]
    public void MotionMaskSimd_ApplyMotionMask_MatchesScalarForVectorAndTailPixels()
    {
        RequireAvx2();

        var source = new byte[]
        {
            20, 30, 40,
            45, 50, 55,
            80, 90, 100,
            120, 120, 120,
            180, 170, 160,
            220, 210, 200,
            12, 80, 140,
            200, 40, 30,
            90, 95, 100,
            240, 235, 230
        };
        var reference = new byte[]
        {
            200, 190, 180,
            45, 51, 54,
            30, 40, 50,
            122, 122, 122,
            20, 25, 30,
            225, 215, 205,
            140, 80, 12,
            199, 41, 31,
            10, 15, 20,
            245, 236, 231
        };

        using var scalarImage = new RowImageProxy(10, 1, source);
        using var simdImage = new RowImageProxy(10, 1, source);
        using var referenceImage = new RowImageProxy(10, 1, reference);

        MotionMask.ApplyMotionMask(scalarImage, referenceImage, threshold: 0.5f, alphaMotion: 12f, midTonePower: 2f);
        MotionMaskSimd.ApplyMotionMask(simdImage, referenceImage, threshold: 0.5f, alphaMotion: 12f, midTonePower: 2f);

        Assert.That(simdImage.LoadRow(0), Is.EqualTo(scalarImage.LoadRow(0)));
    }

    private static void RequireAvx2()
    {
        if (!Avx2.IsSupported)
        {
            Assert.Ignore("AVX2 is not supported on this machine.");
        }
    }

    private sealed class RowImageProxy : IImageProxy
    {
        private byte[] row;

        public RowImageProxy(int width, int height, byte[] row)
        {
            this.Width = width;
            this.Height = height;
            this.row = (byte[])row.Clone();
        }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public double? ExposureTime => null;

        public double? FNumber => null;

        public double? ShutterSpeedValue => null;

        public double? AppertureValue => null;

        public double? IsoSpeedRating => null;

        public double? ExposureBiasValue => null;

        public string? CameraMake => null;

        public string? CameraModel => null;

        public IImageProcessor ImageProcessor => throw new NotSupportedException();

        public byte[] LoadRow(int row) => (byte[])this.row.Clone();

        public void SaveRow(int row, byte[] pixels) => this.row = (byte[])pixels.Clone();

        public byte[] GetPixel(int x, int y)
        {
            var offset = x * 3;
            return [this.row[offset], this.row[offset + 1], this.row[offset + 2]];
        }

        public void SetPixel(int x, int y, byte[] pixel)
        {
            var offset = x * 3;
            this.row[offset] = pixel[0];
            this.row[offset + 1] = pixel[1];
            this.row[offset + 2] = pixel[2];
        }

        public void Create(int width, int height)
        {
            this.Width = width;
            this.Height = height;
            this.row = new byte[width * 3];
        }

        public void LoadFullImage(Span<byte> bytes) => this.row.CopyTo(bytes);

        public void SaveFullImage(byte[] bytes) => this.row = (byte[])bytes.Clone();

        public IImageProxy Clone(Rectangle rectangle) => new RowImageProxy(rectangle.Width, rectangle.Height, this.row);

        public IImageProxy Clone() => new RowImageProxy(this.Width, this.Height, this.row);

        public void Dispose()
        {
        }

        public void SaveAsJpeg(string fileName) => throw new NotSupportedException();

        public void SaveAsJpeg(Stream stream) => throw new NotSupportedException();

        public void Load(Stream stream) => throw new NotSupportedException();

        public void Load(string fileName) => throw new NotSupportedException();
    }
}
