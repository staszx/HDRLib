// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using System.Runtime.Intrinsics;
using Factories;
using Image;
using Interfaces;
using Post;
using PostProcessors;
using Settings;

internal abstract class ToneMapper : IHdrImageProcessor
{
    private readonly WhiteBalancer whiteBalancer = new();

    /// <summary>
/// Initializes a new instance with the specified settings.
/// </summary>
/// <param name="settings">Tone‑mapper configuration.</param>
protected ToneMapper(ToneMapperSettings settings)
    {
        this.Settings = settings;
    }

    /// <summary>
/// Gets the tone‑mapper configuration settings.
/// </summary>
    protected ToneMapperSettings Settings { get; }

    protected Rgb[]? SourcePixelsBeforeProcessing { get; private set; }

    public void ApplyInPlace(Image<Rgb> image)
    {
        this.ApplyInPlace(image, forceCore: false);
    }

    internal void ApplyHdrInPlace(Image<Rgb> image)
    {
        this.ApplyInPlace(image, forceCore: true);
    }

    private void ApplyInPlace(Image<Rgb> image, bool forceCore)
    {
        if (image.Length == 0)
        {
            return;
        }

        if (!forceCore && this.Settings.IsNeutral())
        {
            return;
        }

        this.ForceToneMappingCore = forceCore;
        try
        {
            var saturationRanges = this.Settings.GetSaturationColorRanges();
            this.SourcePixelsBeforeProcessing = this.PreservesSourceBeforeProcessing || saturationRanges.Length != 0
                ? (Rgb[])image.Pixels.Clone()
                : null;

            if (this.NormalizesInputRange)
            {
                ToneMapperUtilities.NormalizeInputRange(image.Pixels);
            }

            var originalPixels = forceCore ? null : CreateBlendSource(image.Pixels, this.Settings.Transparent);

            if (this.Settings.WhiteBalanceReferenceType != WhiteBalanceReferenceType.None)
            {
                this.whiteBalancer.ApplyInPlace(image, this.Settings.WhiteBalanceReferenceType, this.Settings.WhiteBalanceReferenceColor);
            }

            var effectiveSettings = this.BuildEffectiveSettings(image);
            var applyCore = forceCore || this.ShouldApplyCore();
            if (applyCore)
            {
                this.ApplyInPlace(image, effectiveSettings);
            }

            if (forceCore)
            {
                originalPixels = CreateBlendSource(image.Pixels, this.Settings.Transparent);
            }

            ToneBoostProcessor.ApplyInPlace(image.Pixels, this.Settings.ShadowsBoost, this.Settings.MidtonesBoost, this.Settings.HighlightsBoost);
            DehazeProcessor.ApplyInPlace(image, this.Settings.Dehaze);
            LocalContrastProcessor.ApplyInPlace(image, effectiveSettings.LocalContrast, effectiveSettings.LocalContrastRadius);
            this.ApplyColorTemperature(image);
            this.ApplyPostProcess(image, includeCommonSettings: !applyCore);
            if (!applyCore)
            {
                SaturationRangeProcessor.ApplyInPlace(image.Pixels, this.SourcePixelsBeforeProcessing, saturationRanges);
            }

            ApplyBlending(image.Pixels, originalPixels, this.Settings.Transparent);
        }
        finally
        {
            this.SourcePixelsBeforeProcessing = null;
            this.ForceToneMappingCore = false;
        }
    }

    public void Save(Stream stream)
    {
        this.Settings.Save(stream);
    }

    public void Save(string path)
    {
        this.Settings.Save(path);
    }

    public static ToneMapper Load(Stream stream)
    {
        return (ToneMapper)ToneMapperFactory.Create(ToneMapperSettings.Load(stream));
    }

    public static ToneMapper Load(string path)
    {
        return (ToneMapper)ToneMapperFactory.Create(ToneMapperSettings.Load(path));
    }

    /// <summary>
/// Applies the tone‑mapping algorithm to the image using pre‑computed effective settings.
/// </summary>
/// <param name="image">Target image.</param>
/// <param name="effectiveSettings">Computed settings derived from user configuration and auto‑adjust.</param>
protected abstract void ApplyInPlace(Image<Rgb> image, EffectiveToneMapperSettings effectiveSettings);

    protected virtual bool NormalizesInputRange => false;

    protected virtual bool PreservesSourceBeforeProcessing => false;

    protected bool ForceToneMappingCore { get; private set; }

    /// <summary>
/// Helper that prepares SIMD buffers, invokes the core processing delegate, and writes results back to the image.
/// </summary>
/// <param name="image">Image to process.</param>
/// <param name="core">Delegate performing the SIMD operation.</param>
protected void ApplyUsingSimd(Image<Rgb> image, Action<Vector256<float>[][], int, int> core)
    {
        var pixelCount = (int)image.Length;
        var width = image.Width > 0 && image.Height > 0 && image.Width * image.Height == pixelCount
            ? image.Width
            : pixelCount;
        var height = image.Width > 0 && image.Height > 0 && image.Width * image.Height == pixelCount
            ? image.Height
            : 1;
        var vectorCount = (pixelCount + Vector256<float>.Count - 1) / Vector256<float>.Count;
        var pixels = new[]
        {
            new Vector256<float>[vectorCount],
            new Vector256<float>[vectorCount],
            new Vector256<float>[vectorCount]
        };

        ToneMapperSIMDHelper.FromImage(image, pixels);
        core(pixels, width, height);
        for (var i = 0; i < pixelCount; i++)
        {
            var vectorIndex = i / Vector256<float>.Count;
            var lane = i % Vector256<float>.Count;
            image.Pixels[i] = new Rgb(pixels[0][vectorIndex][lane], pixels[1][vectorIndex][lane], pixels[2][vectorIndex][lane]);
        }
    }

    private EffectiveToneMapperSettings BuildEffectiveSettings(Image<Rgb> image)
    {
        var effectiveExposureEv = this.Settings.ExposureEV;
        var effectiveBrightness = this.Settings.Brightness;
        var effectiveContrast = this.Settings.Contrast;
        var effectiveLocalContrast = this.Settings.LocalContrast;
        var effectiveLocalContrastRadius = this.Settings.LocalContrastRadius;
        var effectiveSaturation = SaturationToMultiplier(this.Settings.Saturation);
        var effectiveGamma = this.Settings.Gamma;

        return new EffectiveToneMapperSettings(
            effectiveExposureEv,
            effectiveBrightness,
            effectiveContrast,
            effectiveLocalContrast,
            effectiveLocalContrastRadius,
            effectiveSaturation,
            effectiveGamma);
    }

    private bool ShouldApplyCore()
    {
        return !this.Settings.IsCoreNeutral() ||
               MathF.Abs(this.Settings.Gamma - 1f) > 1e-5f;
    }

    /// <summary>
/// Converts a saturation value in the range [-100, 100] to a multiplier used in processing.
/// </summary>
/// <param name="saturation">Saturation adjustment, -100 to 100.</param>
/// <returns>Multiplier factor.</returns>
protected static float SaturationToMultiplier(float saturation)
    {
        var value = Math.Clamp(saturation, -100f, 100f);
        return value <= 0f
            ? 1f + (value / 100f)
            : 1f + (value / 50f);
    }

    private void ApplyPostProcess(Image<Rgb> image, bool includeCommonSettings)
    {
        var postProcessSettings = includeCommonSettings
            ? this.Settings.ToPostProcessSettings().Combine(this.Settings.PostProcess)
            : this.Settings.PostProcess;
        if (this.Settings.AutoAdjustEnabled)
        {
            var auto = ImageAnalyzer.Analyze(image.Pixels);
            postProcessSettings = postProcessSettings.WithAutoAdjust(auto);
        }

        if (postProcessSettings.IsNeutral())
        {
            return;
        }

        var labProcessor = new LabPostProcessor(postProcessSettings);
        labProcessor.ApplyInPlace(image);
    }

    private void ApplyColorTemperature(Image<Rgb> image)
    {
        var temperature = this.Settings.ColorTemperature;
        if (IsNeutralColorTemperature(temperature))
        {
            return;
        }

        var normalized = ColorTemperatureToWarmth(temperature);
        var redScale = 1f + (0.2f * normalized);
        var greenScale = 1f + (0.05f * MathF.Abs(normalized));
        var blueScale = 1f - (0.2f * normalized);

        var pixels = image.Pixels;
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            p.Red = Math.Clamp(p.Red * redScale, 0f, 1f);
            p.Green = Math.Clamp(p.Green * greenScale, 0f, 1f);
            p.Blue = Math.Clamp(p.Blue * blueScale, 0f, 1f);
            pixels[i] = p;
        }
    }

    private static bool IsNeutralColorTemperature(float temperature)
    {
        return MathF.Abs(temperature) <= 1e-6f ||
               MathF.Abs(temperature - 6500f) <= 1e-3f;
    }

    private static float ColorTemperatureToWarmth(float temperature)
    {
        var kelvin = Math.Clamp(temperature, 2000f, 12000f);
        return kelvin < 6500f
            ? (6500f - kelvin) / 4500f
            : -((kelvin - 6500f) / 5500f);
    }

    private static Rgb[]? CreateBlendSource(Rgb[] pixels, float transparent)
    {
        return Math.Clamp(transparent, 0f, 100f) <= 1e-6f
            ? null
            : (Rgb[])pixels.Clone();
    }

    private static void ApplyBlending(Rgb[] pixels, Rgb[]? source, float transparent)
    {
        if (source is null)
        {
            return;
        }

        var sourceWeight = Math.Clamp(transparent, 0f, 100f) / 100f;
        if (sourceWeight <= 1e-6f)
        {
            return;
        }

        var resultWeight = 1f - sourceWeight;
        Parallel.For(0, pixels.Length, i =>
        {
            var original = source[i];
            var result = pixels[i];
            pixels[i] = new Rgb(
                (result.Red * resultWeight) + (original.Red * sourceWeight),
                (result.Green * resultWeight) + (original.Green * sourceWeight),
                (result.Blue * resultWeight) + (original.Blue * sourceWeight));
        });
    }

    /// <summary>
/// Compact immutable container for the effective settings used during processing.
/// </summary>
protected readonly record struct EffectiveToneMapperSettings(float ExposureEV, float Brightness, float Contrast, float LocalContrast, int LocalContrastRadius, float Saturation, float Gamma);
}
