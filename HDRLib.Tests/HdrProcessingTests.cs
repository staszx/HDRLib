// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Tests;

using System.Diagnostics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Align;
using Gpu;
using Hdr.Debevec;
using Interfaces;
using NUnit.Framework;
using PixelProvider.ImageSharp;
using ToneMapping;
using ToneMapping.Settings;

public class HdrProcessingTests
{
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    [Explicit("Long-running HDR integration test. Run explicitly when validating full image processing output.")]
    [TestCase("DSC_5299.JPG", "DSC_5300.JPG", "DSC_5301.JPG", true)]
    [TestCase("DSC_5299.JPG", "DSC_5300.JPG", "DSC_5301.JPG", false)]
    public void ProcessHdrSeries_WithAndWithoutAlignment_ProducesConsistentResults(string imageName1, string imageName2, string imageName3, bool align)
    {
        var path = GetSamplesPath();
        var outputDirectory = GetOutputPath("Hdr");
        Directory.CreateDirectory(outputDirectory);

        var toneMappers = CreateToneMapperSettings();
        foreach (var toneMapper in toneMappers)
        {
            var results = new Dictionary<ProcessingMode, IImageProxy>();

            foreach (var mode in new[] { ProcessingMode.CPU, ProcessingMode.SIMD })
            {
                var image = ProcessHdrSeries(
                    path,
                    [imageName1, imageName2, imageName3],
                    align,
                    mode,
                    toneMapper.Settings.Clone(),
                    imageScale: 0.1f);

                var outputPath = Path.Combine(outputDirectory, $"{imageName1}_{align}_{toneMapper.Name}_{mode}.jpg");
                image.SaveAsJpeg(outputPath);
                TestContext.AddTestAttachment(outputPath);
                results[mode] = image;
            }

            AssertResultsClose(toneMapper.Name, align, ProcessingMode.CPU, results[ProcessingMode.CPU], ProcessingMode.SIMD, results[ProcessingMode.SIMD]);
        }
    }

    [Explicit("Requires a compatible GPU accelerator and is not run in the default public CI test pass.")]
    [TestCase("DSC_7078.JPG", "DSC_7079.JPG", "DSC_7080.JPG", true)]
    public void ProcessHdrSeries_WithGpuNaturalToneMapper_Succeeds(string imageName1, string imageName2, string imageName3, bool align)
    {
        var path = GetSamplesPath();
        var outputDirectory = GetOutputPath("HdrGpu");
        Directory.CreateDirectory(outputDirectory);

        var toneMapperSettings = new NaturalToneMapperSettings(){Brightness = 25, Contrast = 25, Dehaze = 10, Transparent = 15, LocalContrast = 30, LocalContrastRadius = 30, Saturation = 25};

        var mode = ProcessingMode.GPU;
        var image = ProcessHdrSeries(path, [imageName1, imageName2, imageName3], align, mode, toneMapperSettings, imageScale: 0.1f);

        var outputPath = Path.Combine(outputDirectory, $"{imageName1}_{align}_{toneMapperSettings}_{mode}.jpg");
        image.SaveAsJpeg(outputPath);
        TestContext.AddTestAttachment(outputPath);
    }

    [Test]
    [Explicit("Visual HDR example for the DSC_7313/7314/7315 bracket. Run explicitly to regenerate the example output.")]
    public void ProcessHdrSeries_WithContrastBalancerExample_PreservesLakeDetailsAndBrightensForegroundTrees()
    {
        var outputDirectory = GetOutputPath("HdrExamples");
        Directory.CreateDirectory(outputDirectory);

        var settings = new ContrastBalancerToneMapperSettings
        {
            Strength = 0.93f,
            ToneCompression = 0.38f,
            LightingEffect = 0.44f,
            Luminance = 1.9f,
            WhiteClip = 1.68f,
            BlackClip = 0.0f,
            ExposureEV = 0.0f,
            Brightness = 1.02f,
            Contrast = 1.08f,
            ShadowsBoost = 2.25f,
            MidtonesBoost = 1.12f,
            HighlightsBoost = 0.6f,
            LocalContrast = 0.18f,
            LocalContrastRadius = 20,
            Saturation = 4f,
            Gamma = 1.28f
        };

        var image = ProcessHdrSeries(
            GetSamplesPath(),
            ["DSC_7313.JPG", "DSC_7314.JPG", "DSC_7315.JPG"],
            align: true,
            ProcessingMode.CPU,
            settings);

        var outputPath = Path.Combine(outputDirectory, "DSC_7313_7314_7315_ContrastBalancerExample.jpg");
        image.SaveAsJpeg(outputPath);

        var settingsPath = Path.Combine(outputDirectory, "DSC_7313_7314_7315_ContrastBalancerExample.settings.xml");
        settings.Save(settingsPath);

        TestContext.Out.WriteLine($"HDR example output: {outputPath}");
        TestContext.Out.WriteLine($"HDR example settings: {settingsPath}");
        TestContext.AddTestAttachment(outputPath);
        TestContext.AddTestAttachment(settingsPath);

        Assert.That(File.Exists(outputPath), Is.True);
        Assert.That(File.Exists(settingsPath), Is.True);
    }

    [Test]
    [Explicit("Neutral HDR baseline for the DSC_7313/7314/7315 bracket. Run explicitly before tuning tone mapper settings.")]
    public void ProcessHdrSeries_WithNeutralToneMapper_WritesBaseline()
    {
        var outputDirectory = GetOutputPath("HdrExamples");
        Directory.CreateDirectory(outputDirectory);

        var settings = new ContrastBalancerToneMapperSettings().MakeNeutral();
        var image = ProcessHdrSeries(
            GetSamplesPath(),
            ["DSC_7313.JPG", "DSC_7314.JPG", "DSC_7315.JPG"],
            align: true,
            ProcessingMode.CPU,
            settings);

        var outputPath = Path.Combine(outputDirectory, "DSC_7313_7314_7315_NeutralBaseline.jpg");
        image.SaveAsJpeg(outputPath);

        var settingsPath = Path.Combine(outputDirectory, "DSC_7313_7314_7315_NeutralBaseline.settings.xml");
        settings.Save(settingsPath);

        TestContext.Out.WriteLine($"Neutral HDR baseline output: {outputPath}");
        TestContext.Out.WriteLine($"Neutral HDR baseline settings: {settingsPath}");
        TestContext.AddTestAttachment(outputPath);
        TestContext.AddTestAttachment(settingsPath);

        Assert.That(File.Exists(outputPath), Is.True);
        Assert.That(File.Exists(settingsPath), Is.True);
    }

    [Test]
    [Explicit("HDR baseline for the DSC_7313/7314/7315 bracket with tone mapping disabled entirely.")]
    public void ProcessHdrSeries_WithoutToneMapper_WritesUntonemappedHdr()
    {
        var outputDirectory = GetOutputPath("HdrExamples");
        Directory.CreateDirectory(outputDirectory);

        var image = ProcessHdrSeries(
            GetSamplesPath(),
            ["DSC_7313.JPG", "DSC_7314.JPG", "DSC_7315.JPG"],
            align: true,
            ProcessingMode.CPU,
            toneMapperSettings: null);

        var outputPath = Path.Combine(outputDirectory, "DSC_7313_7314_7315_NoToneMapper.jpg");
        image.SaveAsJpeg(outputPath);

        TestContext.Out.WriteLine($"HDR output without tone mapper: {outputPath}");
        TestContext.AddTestAttachment(outputPath);

        Assert.That(File.Exists(outputPath), Is.True);
    }

    private static string GetSamplesPath()
    {
        return Path.Combine(TestContext.CurrentContext.TestDirectory, "Samples");
    }

    private static string GetOutputPath(string name)
    {
        return Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts", name);
    }

    private static IImageProxy ProcessHdrSeries(
        string path,
        string[] imageNames,
        bool align,
        ProcessingMode mode,
        ToneMapperSettings? toneMapperSettings,
        float imageScale = 1f)
    {
        var previousAvxState = SystemHelper.UseAvxState;
        try
        {
            SystemHelper.UseAvxState = mode == ProcessingMode.SIMD ? UseAvxState.Enable : UseAvxState.Disable;
            using var gpu = mode == ProcessingMode.GPU ? CreateGpuContextOrSkip() : null;
            var list = LoadImages(path, imageNames, imageScale);

            if (align)
            {
                var aligner = gpu is not null ? ImageAligner.Create(gpu) : ImageAligner.Create();
                aligner.Process(list);
            }

            var processor = gpu is not null
                ? new HDRProcessor<ImageSharpProxy>(gpu)
                : new HDRProcessor<ImageSharpProxy>();

            return processor.Process(list, new HdrImageOptions
            {
                SampleCount = 1000,
                SmoothFactor = 300,
                MotionFilterStrength = 80,
                ToneMapperSettings = toneMapperSettings
            });
        }
        finally
        {
            SystemHelper.UseAvxState = previousAvxState;
        }
    }

    private static List<IImageProxy> LoadImages(string path, IReadOnlyList<string> imageNames, float imageScale)
    {
        var images = new List<IImageProxy>(imageNames.Count);
        foreach (var imageName in imageNames)
        {
            var image = new ImageSharpProxy();
            image.Load(Path.Combine(path, imageName));
            ResizeIfNeeded(image, imageScale);
            images.Add(image);
        }

        return images;
    }

    private static void ResizeIfNeeded(IImageProxy image, float imageScale)
    {
        if (imageScale >= 0.999f)
        {
            return;
        }

        var vectorSize = Vector256<float>.Count;
        var width = Math.Max(vectorSize, (int)Math.Round(image.Width * imageScale));
        var height = Math.Max(1, (int)Math.Round(image.Height * imageScale));
        width -= width % vectorSize;
        image.ImageProcessor.Resize(width, height);
    }

    private static IReadOnlyList<ToneMapperCase> CreateToneMapperSettings()
    {
        return
        [
            new ToneMapperCase("AcesFilmic", new AcesFilmicTonemapperSettings
            {
                Key = 0.32f,
                Gamma = 1.2f
            }),
            new ToneMapperCase("AutoAdjust", new AutoAdjustTonemapperSettings
            {
                AdjustBrightness = true,
                AdjustContrast = true,
                AdjustSaturation = true,
                TargetLuminance255 = 145f,
                TargetContrastStdDev255 = 85f,
                Gamma = 1.1f
            }),
            new ToneMapperCase("Natural", new NaturalToneMapperSettings
            {
                TargetGray = 0.26f,
                WhitePointPercentile = 0.98f,
                OutputMidGray = 0.28f,
                TonalRangeCompression = 3.5f,
                BypassToneCompressionForLdr = false,
                Gamma = 1.1f
            }),
            new ToneMapperCase("ContrastBalancer", new ContrastBalancerToneMapperSettings
            {
                Strength = 0.85f,
                ToneCompression = 0.75f,
                LightingEffect = 1.15f,
                Luminance = 1.25f,
                WhiteClip = 1.35f,
                BlackClip = 0.05f,
                Gamma = 1.1f
            }),
            new ToneMapperCase("BrightnessBalancer", new BrightnessBalancerToneMapperSettings
            {
                Strength = 0.85f,
                Lighting = 1.1f,
                BrightnessBoost = 1.15f,
                WhiteClip = 1.35f,
                BlackClip = 0.05f,
                Gamma = 1.1f
            })
        ];
    }

    private static void AssertResultsClose(string toneMapperName, bool align, ProcessingMode expectedMode, IImageProxy expected, ProcessingMode actualMode, IImageProxy actual)
    {
        Assert.That(actual.Width, Is.EqualTo(expected.Width), $"{toneMapperName} {align} {actualMode} width");
        Assert.That(actual.Height, Is.EqualTo(expected.Height), $"{toneMapperName} {align} {actualMode} height");

        var expectedBytes = LoadFullImage(expected);
        var actualBytes = LoadFullImage(actual);
        var comparison = Compare(expectedBytes, actualBytes);
        TestContext.Out.WriteLine(
            $"{toneMapperName}, align={align}, {expectedMode}->{actualMode}: mean={comparison.Mean:F3}, max={comparison.Max}, p99={comparison.P99}");

        Assert.Multiple(() =>
        {
            Assert.That(comparison.Mean, Is.LessThanOrEqualTo(6.0), $"{toneMapperName} align={align} {expectedMode}->{actualMode} mean byte diff");
            Assert.That(comparison.P99, Is.LessThanOrEqualTo(18), $"{toneMapperName} align={align} {expectedMode}->{actualMode} p99 byte diff");
            Assert.That(comparison.Max, Is.LessThanOrEqualTo(64), $"{toneMapperName} align={align} {expectedMode}->{actualMode} max byte diff");
        });
    }

    private static byte[] LoadFullImage(IImageProxy image)
    {
        var bytes = new byte[image.Width * image.Height * 3];
        image.LoadFullImage(bytes);
        return bytes;
    }

    private static ImageComparison Compare(byte[] expected, byte[] actual)
    {
        Assert.That(actual, Has.Length.EqualTo(expected.Length));
        var diffs = new int[expected.Length];
        long total = 0;
        var max = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            var diff = Math.Abs(expected[i] - actual[i]);
            diffs[i] = diff;
            total += diff;
            max = Math.Max(max, diff);
        }

        Array.Sort(diffs);
        var p99Index = Math.Clamp((int)Math.Ceiling(diffs.Length * 0.99) - 1, 0, diffs.Length - 1);
        return new ImageComparison((double)total / expected.Length, max, diffs[p99Index]);
    }

    private static GpuContext CreateGpuContextOrSkip()
    {
        try
        {
            return new GpuContext(2);
        }
        catch
        {
            try
            {
                return new GpuContext();
            }
            catch (Exception ex)
            {
                Assert.Ignore($"GPU accelerator is not available: {ex.Message}");
                throw;
            }
        }
    }

    private enum ProcessingMode
    {
        CPU,
        SIMD,
        GPU
    }

    private readonly record struct ToneMapperCase(string Name, ToneMapperSettings Settings);

    private readonly record struct ImageComparison(double Mean, int Max, int P99);
}
