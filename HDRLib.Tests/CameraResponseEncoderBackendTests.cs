// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Tests;

using System.Runtime.Intrinsics.X86;
using Gpu;
using Hdr.Debevec;
using Image;
using ILGPU.Runtime;
using NUnit.Framework;

public class CameraResponseEncoderBackendTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void Simd_MatchesScalarIncludingTail(bool normalized)
    {
        if (!Avx2.IsSupported) Assert.Ignore("AVX2 is unavailable.");
        foreach (var response in Responses(normalized))
        {
            var scalar = new CameraResponseEncoder(response, -8d);
            var simd = new CameraResponseEncoderSIMD(scalar, normalized);
            foreach (var length in new[] { 0, 1, 7, 8, 9, 31, 1031 })
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    var input = Values(length, normalized);
                    var actual = input.ToArray();
                    simd.ApplyInPlace(actual, channel);
                    AssertClose(input, actual, scalar, normalized, channel);
                }
            }
        }
    }

    [TestCase(false, 0)]
    [TestCase(true, 0)]
    [TestCase(false, 1)]
    [TestCase(true, 1)]
    [TestCase(false, 2)]
    [TestCase(true, 2)]
    public void Gpu_MatchesScalarIncludingPartialGroup(bool normalized, int device)
    {
        if (GpuContext.GetAccelerators().Count <= device) Assert.Ignore("Accelerator is unavailable.");
        using var context = new GpuContext(device);
        var gpu = new CameraResponseEncoderGpu(context);
        foreach (var response in Responses(normalized))
        {
            var scalar = new CameraResponseEncoder(response, -8d);
            var input = Values(1031, normalized);
            using var buffer = context.Accelerator.Allocate1D(input.Select(v => new Rgb(v, v, v)).ToArray());
            gpu.ApplyInPlace(buffer.View, scalar, normalized);
            var actual = buffer.GetAsArray1D();
            AssertClose(input, actual.Select(p => p.Red).ToArray(), scalar, normalized, 0);
            AssertClose(input, actual.Select(p => p.Green).ToArray(), scalar, normalized, 1);
            AssertClose(input, actual.Select(p => p.Blue).ToArray(), scalar, normalized, 2);
        }
    }

    [Test]
    public void Simd_NormalizedNaN_ThrowsLikeScalar()
    {
        var encoder = new CameraResponseEncoderSIMD(new CameraResponseEncoder(Responses(true).First(), 0), true);
        var input = Enumerable.Repeat(0.5f, 8).ToArray();
        input[4] = float.NaN;
        Assert.Throws<ArgumentOutOfRangeException>(() => encoder.ApplyInPlace(input, 0));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void Accelerated_SubnormalRadiance_MatchesScalar(int device)
    {
        if (GpuContext.GetAccelerators().Count <= device) Assert.Ignore("Accelerator is unavailable.");
        var response = Enumerable.Range(0, 3).Select(_ => Enumerable.Range(0, 256)
            .Select(z => -110d + 30d * z / 255d).ToArray()).ToArray();
        var scalar = new CameraResponseEncoder(response, 0d);
        float[] input = [float.Epsilon, 1e-44f, 1e-42f, 1e-40f, 1e-39f, 1e-38f, 1.17549435e-38f, 1e-37f];
        var simdValues = input.ToArray();
        new CameraResponseEncoderSIMD(scalar, false).ApplyInPlace(simdValues, 0);
        AssertClose(input, simdValues, scalar, false, 0);
        using var context = new GpuContext(device);
        using var buffer = context.Accelerator.Allocate1D(input.Select(v => new Rgb(v, v, v)).ToArray());
        new CameraResponseEncoderGpu(context).ApplyInPlace(buffer.View, scalar, false);
        AssertClose(input, buffer.GetAsArray1D().Select(p => p.Red).ToArray(), scalar, false, 0);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void Gpu_NormalizedNaN_ThrowsLikeScalar(int device)
    {
        if (GpuContext.GetAccelerators().Count <= device) Assert.Ignore("Accelerator is unavailable.");
        using var context = new GpuContext(device);
        var gpu = new CameraResponseEncoderGpu(context);
        using var buffer = context.Accelerator.Allocate1D(new[] { new Rgb(0.2f, float.NaN, 0.8f) });
        var scalar = new CameraResponseEncoder(Responses(true).First(), 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => gpu.ApplyInPlace(buffer.View, scalar, true));
    }

    private static IEnumerable<double[][]> Responses(bool normalized)
    {
        double[][] Curve() => Enumerable.Range(0, 3).Select(c => Enumerable.Range(0, 256)
            .Select(z => (1.2d + c * 0.6d) * Math.Log(Math.Max(z, 1) / 128d) + c * 0.4d).ToArray()).ToArray();
        yield return Curve();
        var plateau = Curve();
        foreach (var curve in plateau)
        {
            for (var z = 80; z <= 100; z++) curve[z] = curve[80];
            curve[254] = curve[252];
        }
        yield return plateau;
        yield return Enumerable.Range(0, 3).Select(c => Enumerable.Repeat((double)c, 256).ToArray()).ToArray();
        yield return Enumerable.Range(0, 3).Select(c => Enumerable.Range(0, 256).Select(z => c + z * 1e-11d).ToArray()).ToArray();
        if (normalized)
        {
            var offset = Curve();
            for (var c = 0; c < 3; c++)
                for (var z = 0; z < 256; z++) offset[c][z] += (c - 1) * 1000d;
            yield return offset;
        }
    }

    private static float[] Values(int length, bool normalized)
    {
        var random = new Random(9532);
        var result = Enumerable.Range(0, length).Select(_ => normalized
            ? (float)(random.NextDouble() * 1.2d)
            : (float)Math.Exp(-10d + random.NextDouble() * 25d)).ToArray();
        float[] boundaries = [float.NegativeInfinity, -1f, 0f, float.Epsilon, 1e-38f, 0.01f, 0.18f, 0.5f, 1f, float.PositiveInfinity];
        for (var i = 0; i < Math.Min(boundaries.Length, length); i++) result[i] = boundaries[i];
        return result;
    }

    private static void AssertClose(float[] input, float[] actual, CameraResponseEncoder scalar, bool normalized, int channel)
    {
        var maximumErrorInCodes = 0f;
        for (var i = 0; i < input.Length; i++)
        {
            var expected = normalized ? scalar.EncodeNormalized(input[i], channel) : scalar.Encode(input[i], channel);
            Assert.That(float.IsFinite(actual[i]), Is.True, $"channel={channel}, input={input[i]}");
            maximumErrorInCodes = Math.Max(maximumErrorInCodes, Math.Abs(expected - actual[i]) * (normalized ? 255f : 1f));
        }
        Assert.That(maximumErrorInCodes, Is.LessThan(0.001f), $"channel={channel}, length={input.Length}, normalized={normalized}");
    }
}
