// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Tests;

using System.Runtime.Intrinsics;
using HDRLib.Gpu;
using HDRLib.Image;
using HDRLib.ToneMapping;
using HDRLib.ToneMapping.Factories;
using HDRLib.ToneMapping.Settings;
using ILGPU.Runtime;
using NUnit.Framework;
using HdrImage = HDRLib.Image.Image<HDRLib.Image.Rgb>;

public class ToneMapperHdrBackendTests
{
    private static IEnumerable<TestCaseData> ToneMapperSettings()
    {
        yield return new TestCaseData(new AcesFilmicTonemapperSettings { Key = 0.32f, Gamma = 1.2f, Clarity = 35f })
            .SetName("ApplyHdrInPlace_AcesFilmic_PreservesDetailsAcrossBackends");
        yield return new TestCaseData(new NaturalToneMapperSettings
        {
            AutoAdjustEnabled = true,
            AutoAdjustStrength = 35f,
            TargetGray = 0.26f,
            WhitePointPercentile = 0.98f,
            OutputMidGray = 0.28f,
            TonalRangeCompression = 3.5f,
            BypassToneCompressionForLdr = false,
            Gamma = 1.1f
        }).SetName("ApplyHdrInPlace_Natural_PreservesDetailsAcrossBackends");
        yield return new TestCaseData(new ContrastBalancerToneMapperSettings
        {
            Strength = 0.85f,
            ToneCompression = 0.75f,
            LightingEffect = 1.15f,
            Luminance = 1.25f,
            WhiteClip = 1.35f,
            BlackClip = 0.05f,
            Gamma = 1.1f
        }).SetName("ApplyHdrInPlace_ContrastBalancer_PreservesDetailsAcrossBackends");
        yield return new TestCaseData(new BrightnessBalancerToneMapperSettings
        {
            Strength = 0.85f,
            Lighting = 1.1f,
            BrightnessBoost = 1.15f,
            WhiteClip = 1.35f,
            BlackClip = 0.05f,
            Gamma = 1.1f
        }).SetName("ApplyHdrInPlace_BrightnessBalancer_PreservesDetailsAcrossBackends");
    }

    [TestCaseSource(nameof(ToneMapperSettings))]
    public void ApplyHdrInPlace_PreservesDetailsAcrossBackends(ToneMapperSettings settings)
    {
        settings.WhiteBalanceReferenceType = WhiteBalanceReferenceType.Auto;
        var source = CreateHdrRadianceImage();

        var cpu = Clone(source);
        ((ToneMapper)ToneMapperFactory.Create(settings.Clone())).ApplyHdrInPlace(cpu, sceneAverageBrightness: 0.35f);

        var vectors = CreateVectors(source);
        ToneMapperFactorySIMD.Create(settings.Clone()).ApplyHdrInPlace(vectors, source.Width, source.Height, sceneAverageBrightness: 0.35f);
        var simd = ToneMapperSIMDHelper.ToImage(vectors, source.Width, source.Height);

        using var context = CreateGpuContextOrSkip();
        var gpu = ApplyGpu(context, settings.Clone(), source, sceneAverageBrightness: 0.35f);

        Assert.Multiple(() =>
        {
            AssertPreservesDetails(cpu, "CPU");
            AssertPreservesDetails(simd, "SIMD");
            AssertPreservesDetails(gpu, "GPU");
            Assert.That(MeanAbsoluteDifference(cpu, simd), Is.LessThan(0.015f), "CPU -> SIMD");
            Assert.That(MeanAbsoluteDifference(cpu, gpu), Is.LessThan(0.015f), "CPU -> GPU");
        });
    }

    private static HdrImage CreateHdrRadianceImage()
    {
        var image = new HdrImage(8, 1)
        {
            Width = 8,
            Height = 1,
            Pixels =
            [
                new Rgb(2f, 1.6f, 1.2f),
                new Rgb(4f, 3.2f, 2.4f),
                new Rgb(8f, 6.4f, 4.8f),
                new Rgb(16f, 12.8f, 9.6f),
                new Rgb(24f, 28f, 20f),
                new Rgb(36f, 42f, 30f),
                new Rgb(54f, 63f, 45f),
                new Rgb(81f, 94.5f, 67.5f)
            ]
        };

        for (var i = 0; i < image.Pixels.Length; i++)
        {
            image.Pixels[i] *= 1000f;
        }

        return image;
    }

    private static Vector256<float>[][] CreateVectors(HdrImage image)
    {
        var vectorCount = (image.Pixels.Length + Vector256<float>.Count - 1) / Vector256<float>.Count;
        var vectors = new[]
        {
            new Vector256<float>[vectorCount],
            new Vector256<float>[vectorCount],
            new Vector256<float>[vectorCount]
        };
        ToneMapperSIMDHelper.FromImage(image, vectors);
        return vectors;
    }

    private static HdrImage ApplyGpu(
        GpuContext context,
        ToneMapperSettings settings,
        HdrImage source,
        float sceneAverageBrightness)
    {
        var result = Clone(source);
        using var pixels = context.Accelerator.Allocate1D<Rgb>(result.Pixels.Length);
        pixels.CopyFromCPU(result.Pixels);
        ToneMapperFactoryGpu.Create(context, settings)
            .ApplyHdrInPlace(pixels.View, result.Width, result.Height, sceneAverageBrightness);
        result.Pixels = pixels.GetAsArray1D();
        return result;
    }

    private static void AssertPreservesDetails(HdrImage image, string backend)
    {
        Assert.That(image.Pixels, Has.All.Matches<Rgb>(pixel =>
            float.IsFinite(pixel.Red) &&
            float.IsFinite(pixel.Green) &&
            float.IsFinite(pixel.Blue)), $"{backend} finite pixels");

        var min = image.Pixels.Min(pixel => pixel.Light());
        var max = image.Pixels.Max(pixel => pixel.Light());
        Assert.That(max - min, Is.GreaterThan(0.1f), $"{backend} dynamic range");
    }

    private static float MeanAbsoluteDifference(HdrImage expected, HdrImage actual)
    {
        var sum = 0f;
        for (var i = 0; i < expected.Pixels.Length; i++)
        {
            sum += MathF.Abs(expected.Pixels[i].Red - actual.Pixels[i].Red);
            sum += MathF.Abs(expected.Pixels[i].Green - actual.Pixels[i].Green);
            sum += MathF.Abs(expected.Pixels[i].Blue - actual.Pixels[i].Blue);
        }

        return sum / (expected.Pixels.Length * 3);
    }

    private static HdrImage Clone(HdrImage image)
    {
        return new HdrImage(image.Width, image.Height)
        {
            Width = image.Width,
            Height = image.Height,
            Pixels = (Rgb[])image.Pixels.Clone()
        };
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
}
