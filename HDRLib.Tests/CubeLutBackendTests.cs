namespace HDRLib.Tests;

using System.Diagnostics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using HDRLib.ColorGrading;
using HDRLib.Gpu;
using HDRLib.Hdr.Debevec;
using HDRLib.Image;
using HDRLib.PixelProvider.ImageSharp;
using HDRLib.ToneMapping.Settings;
using ILGPU.Runtime;
using NUnit.Framework;

public class CubeLutBackendTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void Simd_MatchesScalarAtEdgesAndAcrossPartialBlocks(bool is3D)
    {
        foreach (var specialDomain in new[] { false, true })
        {
            var lut = CreateLut(is3D, specialDomain);
            foreach (var length in new[] { 0, 1, 3, 4, 5, 7, 8, 9, 1023, 1024, 1025, 2051 })
            foreach (var strength in new[] { 0f, 0.37f, 1f })
            foreach (var scale in new[] { 1f, 255f })
            {
                var source = Values(length, scale, specialDomain);
                var expected = source.ToArray();
                var actual = source.ToArray();
                lut.ApplyInPlace(expected, strength, scale);
                CubeLutSIMD.ApplyInPlace(lut, actual, strength, scale);
                AssertClose(expected, actual);
            }
        }
    }

    public static IEnumerable<int> Devices() => Enumerable.Range(0, GpuContext.GetAccelerators().Count);

    [TestCaseSource(nameof(Devices))]
    public void Gpu_MatchesScalarIncludingNonfiniteAndTinyDomains(int device)
    {
        using var context = new GpuContext(device);
        TestContext.Out.WriteLine($"GPU device: {context.Accelerator}");
        foreach (var is3D in new[] { false, true })
        foreach (var tiny in new[] { false, true })
        {
            var lut = CreateLut(is3D, tiny);
            using var gpu = new CubeLutGpu(context, lut);
            using var buffer = context.Accelerator.Allocate1D<Rgb>(1031);
            foreach (var strength in new[] { 0f, 0.37f, 1f })
            foreach (var scale in new[] { 1f, 255f })
            {
                var source = Values(1031, scale, tiny);
                var expected = source.ToArray();
                lut.ApplyInPlace(expected, strength, scale);
                buffer.CopyFromCPU(source);
                gpu.ApplyInPlace(buffer.View, strength, scale);
                AssertClose(expected, buffer.GetAsArray1D());
            }
        }
    }

    [TestCaseSource(nameof(Devices))]
    public void Gpu_QuantizedHdrInputMatchesByteImageContract(int device)
    {
        using var context = new GpuContext(device);
        var lut = CreateLut(true, false);
        var values = new[] { -5f, 0, 0.5f, 1.5f, 2.5f, 3.5f, 127.5f, 128.5f, 254.5f, 255, 256 };
        var source = values.Select(v => new Rgb(v, v, v)).ToArray();
        var expected = values.Select(v => new Rgb(Math.Clamp(MathF.Round(v), 0, 255), Math.Clamp(MathF.Round(v), 0, 255), Math.Clamp(MathF.Round(v), 0, 255))).ToArray();
        lut.ApplyInPlace(expected, 0.37f, 255);
        using var gpu = new CubeLutGpu(context, lut);
        using var buffer = context.Accelerator.Allocate1D(source);
        gpu.ApplyInPlace(buffer.View, 0.37f, 255, quantizeInput: true);
        AssertClose(expected, buffer.GetAsArray1D());
    }

    [TestCaseSource(nameof(Devices))]
    public void SingleImage_ReusesAndReplacesGpuLutWithoutAccumulation(int device)
    {
        using var context = new GpuContext(device);
        using var source = new ImageSharpProxy();
        source.Create(17, 3);
        for (var y = 0; y < source.Height; y++)
            source.SaveRow(y, Enumerable.Range(0, source.Width * 3).Select(i => (byte)(i * 4)).ToArray());
        using var processor = new SingleImageProcessor(context);
        processor.LoadSource(source);
        var settings = new BrightnessBalancerToneMapperSettings { WhiteClip = 0.8f };
        processor.Process(settings);
        using var baseline = processor.ToImage<ImageSharpProxy>();
        foreach (var is3D in new[] { false, false, true, false })
        {
            var lut = CreateLut(is3D, false);
            for (var pass = 0; pass < 2; pass++)
            {
                using var expected = baseline.Clone();
                lut.ApplyInPlace(expected, 0.75f);
                processor.ProcessWithLut(settings, lut, 0.75f);
                using var actual = processor.ToImage<ImageSharpProxy>();
                for (var y = 0; y < actual.Height; y++)
                {
                    var e = expected.LoadRow(y);
                    var a = actual.LoadRow(y);
                    for (var i = 0; i < a.Length; i++) Assert.That((int)a[i], Is.EqualTo(e[i]).Within(2));
                }
            }
        }
    }

    [Test]
    public void Simd_EncodedImageMatchesScalarWithOddWidth()
    {
        using var expected = new ImageSharpProxy();
        expected.Create(17, 3);
        for (var y = 0; y < expected.Height; y++) expected.SaveRow(y, Enumerable.Range(0, 51).Select(i => (byte)(i * 5)).ToArray());
        using var actual = expected.Clone();
        var lut = CreateLut(true, false);
        lut.ApplyInPlace(expected, 0.71f);
        CubeLutSIMD.ApplyInPlace(lut, actual, 0.71f);
        for (var y = 0; y < actual.Height; y++) Assert.That(actual.LoadRow(y), Is.EqualTo(expected.LoadRow(y)));
    }

    [Test, Explicit("Manual timing, not a performance assertion.")]
    public void Benchmark()
    {
        var lut = CreateLut(true, false);
        lut = CreateLut(true, false, 33);
        var source = Values(1_000_003, 1, false);
        var work = source.ToArray();
        lut.ApplyInPlace(work);
        CubeLutSIMD.ApplyInPlace(lut, work);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++) { source.CopyTo(work, 0); lut.ApplyInPlace(work); }
        TestContext.Out.WriteLine($"Scalar ms/frame: {watch.Elapsed.TotalMilliseconds / 3:0.00}");
        watch.Restart();
        for (var i = 0; i < 3; i++) { source.CopyTo(work, 0); CubeLutSIMD.ApplyInPlace(lut, work); }
        TestContext.Out.WriteLine($"AVX2 supported: {Avx2.IsSupported}; SIMD ms/frame: {watch.Elapsed.TotalMilliseconds / 3:0.00}");
        foreach (var device in Devices())
        {
            if (GpuContext.GetAccelerators()[device].AcceleratorType.ToString() == "CPU") continue;
            using var context = new GpuContext(device);
            using var gpu = new CubeLutGpu(context, lut);
            using var buffer = context.Accelerator.Allocate1D(source);
            gpu.ApplyInPlace(buffer.View);
            watch.Restart();
            for (var i = 0; i < 3; i++) gpu.ApplyInPlace(buffer.View);
            TestContext.Out.WriteLine($"{context.Accelerator}: kernel ms/frame (resident buffer): {watch.Elapsed.TotalMilliseconds / 3:0.00}");
        }
    }

    private static CubeLut CreateLut(bool is3D, bool tiny, int size = 5)
    {
        var text = new StringBuilder(is3D ? $"LUT_3D_SIZE {size}\n" : $"LUT_1D_SIZE {size}\n");
        text.Append(tiny ? "DOMAIN_MIN 0 0 0\nDOMAIN_MAX 1e-38 1e-38 1e-38\n" : "DOMAIN_MIN -0.25 -0.5 -0.75\nDOMAIN_MAX 1.25 1.5 1.75\n");
        for (var b = 0; b < (is3D ? size : 1); b++)
        for (var g = 0; g < (is3D ? size : 1); g++)
        for (var r = 0; r < size; r++)
        {
            var red = r / (float)(size - 1); var green = is3D ? g / (float)(size - 1) : red; var blue = is3D ? b / (float)(size - 1) : red;
            text.AppendLine(FormattableString.Invariant($"{(1 - blue) * 1.2f:R} {red * green:R} {green * green - 0.1f:R}"));
        }

        return CubeLut.Parse(new StringReader(text.ToString()));
    }

    private static Rgb[] Values(int length, float scale, bool tiny)
    {
        var edges = tiny
            ? new[] { 0f, float.Epsilon, 1e-39f, 5e-39f, 1e-38f, float.NaN, float.NegativeInfinity, float.PositiveInfinity }
            : new[] { -2f, -0.25f, 0, 0.5f, 1, 1.75f, 3f, float.NaN, float.NegativeInfinity, float.PositiveInfinity };
        var random = new Random(12);
        return Enumerable.Range(0, length).Select(i => i < edges.Length * 3
            ? new Rgb(edges[i % edges.Length], edges[(i + 1) % edges.Length], edges[(i + 2) % edges.Length]) * scale
            : new Rgb(random.NextSingle(), random.NextSingle(), random.NextSingle()) * (tiny ? 1e-38f * scale : scale)).ToArray();
    }

    private static void AssertClose(Rgb[] expected, Rgb[] actual)
    {
        Assert.That(actual.Length, Is.EqualTo(expected.Length));
        for (var i = 0; i < actual.Length; i++)
        {
            Check(expected[i].Red, actual[i].Red, i, "R");
            Check(expected[i].Green, actual[i].Green, i, "G");
            Check(expected[i].Blue, actual[i].Blue, i, "B");
        }
    }

    private static void Check(float expected, float actual, int index, string channel)
    {
        if (float.IsNaN(expected)) Assert.That(float.IsNaN(actual), Is.True, $"{index}/{channel}");
        else if (float.IsInfinity(expected)) Assert.That(actual, Is.EqualTo(expected), $"{index}/{channel}");
        else Assert.That(actual, Is.EqualTo(expected).Within(3e-6f * Math.Max(1, Math.Abs(expected))), $"{index}/{channel}");
    }
}
