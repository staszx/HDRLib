// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using HDRLib.Gpu;
using HDRLib.Image;
using HDRLib.Post;
using HDRLib.PostProcessors;
using HDRLib.ToneMapping.Settings;
using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;

internal abstract class ToneMapperGpu : IToneMapperGpu
{
    private readonly WhiteBalancerGpu whiteBalancer;
    private readonly ImageAnalyzerGpu imageAnalyzer;
    private readonly LabPostProcessorGpu labPostProcessor;
    private readonly DehazeProcessorGpu dehazeProcessor;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float> colorTemperatureKernel;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float> toneBoostKernel;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>, int, int> localContrastHorizontalKernel;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>, int, int, float, int> localContrastVerticalKernel;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>> copyKernel;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>, float> blendKernel;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>> inputStatsKernel;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float> inputScaleKernel;
    private readonly Accelerator accelerator;
    private MemoryBuffer1D<Rgb, Stride1D.Dense>? blendBuffer;
    private MemoryBuffer1D<Rgb, Stride1D.Dense>? tempBuffer;
    private MemoryBuffer1D<Rgb, Stride1D.Dense>? localContrastBuffer;

    /// <summary>
/// Initializes a new GPU‑based tone mapper with the given context and settings.
/// </summary>
/// <param name="context">GPU execution context.</param>
/// <param name="settings">Tone‑mapper configuration.</param>
protected ToneMapperGpu(GpuContext context, ToneMapperSettings settings)
    {
        this.accelerator = context.Accelerator;
        this.whiteBalancer = new WhiteBalancerGpu(context);
        this.imageAnalyzer = new ImageAnalyzerGpu(context);
        this.labPostProcessor = new LabPostProcessorGpu(context);
        this.dehazeProcessor = new DehazeProcessorGpu(context);
        this.colorTemperatureKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float>(ApplyColorTemperatureKernel);
        this.toneBoostKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float, float, float>(ApplyToneBoostKernel);
        this.localContrastHorizontalKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>, int, int>(ApplyLocalContrastHorizontalKernel);
        this.localContrastVerticalKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>, int, int, float, int>(ApplyLocalContrastVerticalKernel);
        this.copyKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>>(CopyKernel);
        this.blendKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>, float>(BlendKernel);
        this.inputStatsKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>>(ToneMapperUtilities.InputStatsKernel);
        this.inputScaleKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, float>(ApplyInputScaleKernel);
        this.Settings = settings;
    }

    /// <summary>
/// Gets the tone‑mapper configuration settings.
/// </summary>
protected ToneMapperSettings Settings { get; }

    protected ArrayView1D<Rgb, Stride1D.Dense> SourcePixelsBeforeProcessing { get; private set; }

    public void Dispose()
    {
        this.blendBuffer?.Dispose();
        this.tempBuffer?.Dispose();
        this.localContrastBuffer?.Dispose();
        this.blendBuffer = null;
        this.tempBuffer = null;
        this.localContrastBuffer = null;
        GC.SuppressFinalize(this);
    }

    public virtual void ApplyInPlace(ArrayView1D<Rgb, Stride1D.Dense> gpuPixels, int width, int height)
    {
        if (gpuPixels.Length == 0)
        {
            return;
        }

        if (this.Settings.IsNeutral())
        {
            return;
        }

        if (this.PreservesSourceBeforeProcessing)
        {
            var source = this.GetTempBuffer(gpuPixels.Length).View;
            this.copyKernel((int)gpuPixels.Length, gpuPixels, source);
            this.SourcePixelsBeforeProcessing = source;
        }

        var applyCore = this.ShouldApplyCore();
        if (applyCore && this.NormalizesInputRange)
        {
            this.NormalizeInputRange(gpuPixels);
        }

        var shouldBlend = ShouldBlend(this.Settings.Transparent);
        var original = shouldBlend
            ? this.GetBlendBuffer(gpuPixels.Length).View
            : default;
        if (shouldBlend)
        {
            this.copyKernel((int)gpuPixels.Length, gpuPixels, original);
        }

        if (this.Settings.WhiteBalanceReferenceType != WhiteBalanceReferenceType.None)
        {
            this.whiteBalancer.ApplyInPlace(gpuPixels, this.Settings.WhiteBalanceReferenceType, this.Settings.WhiteBalanceReferenceColor);
        }

        var effectiveSettings = this.BuildEffectiveSettings(gpuPixels);
        if (applyCore)
        {
            this.ApplyInPlace(gpuPixels, effectiveSettings);
        }

        this.ApplyToneBoost(gpuPixels);
        this.dehazeProcessor.ApplyInPlace(gpuPixels, this.Settings.Dehaze);
        this.ApplyLocalContrast(gpuPixels, width, height, effectiveSettings.LocalContrast, effectiveSettings.LocalContrastRadius);
        this.ApplyColorTemperature(gpuPixels);
        this.ApplyPostProcess(gpuPixels, includeCommonSettings: !applyCore);
        if (shouldBlend)
        {
            this.blendKernel((int)gpuPixels.Length, original, gpuPixels, Math.Clamp(this.Settings.Transparent, 0f, 100f) / 100f);
        }
    }

    /// <summary>
/// Applies the tone‑mapping algorithm on GPU pixel data using pre‑computed settings.
/// </summary>
/// <param name="gpuPixels">GPU pixel buffer.</param>
/// <param name="effectiveSettings">Effective settings derived from configuration.</param>
protected abstract void ApplyInPlace(ArrayView1D<Rgb, Stride1D.Dense> gpuPixels, EffectiveToneMapperSettings effectiveSettings);

    protected virtual bool NormalizesInputRange => true;

    protected virtual bool PreservesSourceBeforeProcessing => false;

    private EffectiveToneMapperSettings BuildEffectiveSettings(ArrayView1D<Rgb, Stride1D.Dense> gpuPixels)
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

    protected static float SaturationToMultiplier(float saturation)
    {
        var value = Math.Clamp(saturation, -100f, 100f);
        return value <= 0f
            ? 1f + (value / 100f)
            : 1f + (value / 50f);
    }

    private bool ShouldApplyCore()
    {
        return !this.Settings.IsCoreNeutral() ||
               MathF.Abs(this.Settings.Gamma - 1f) > 1e-5f;
    }

    private void ApplyPostProcess(ArrayView1D<Rgb, Stride1D.Dense> gpuPixels, bool includeCommonSettings)
    {
        var postProcessSettings = includeCommonSettings
            ? this.Settings.ToPostProcessSettings().Combine(this.Settings.PostProcess)
            : this.Settings.PostProcess;
        if (this.Settings.AutoAdjustEnabled)
        {
            var auto = this.imageAnalyzer.Analyze(gpuPixels);
            postProcessSettings = postProcessSettings.WithAutoAdjust(auto);
        }

        if (postProcessSettings.IsNeutral())
        {
            return;
        }

        this.labPostProcessor.ApplyInPlace(gpuPixels, gpuPixels.Length, postProcessSettings);
    }

    private void NormalizeInputRange(ArrayView1D<Rgb, Stride1D.Dense> gpuPixels)
    {
        var scale = ToneMapperUtilities.ComputeInputScale(this.accelerator, gpuPixels, this.inputStatsKernel);
        if (MathF.Abs(scale - 1f) <= 1e-6f)
        {
            return;
        }

        this.inputScaleKernel((int)gpuPixels.Length, gpuPixels, scale);
    }

    private void ApplyColorTemperature(ArrayView1D<Rgb, Stride1D.Dense> gpuPixels)
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
        this.colorTemperatureKernel((int)gpuPixels.Length, gpuPixels, redScale, greenScale, blueScale);
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

    private void ApplyToneBoost(ArrayView1D<Rgb, Stride1D.Dense> gpuPixels)
    {
        if (ToneBoostProcessor.IsNeutral(this.Settings.ShadowsBoost, this.Settings.MidtonesBoost, this.Settings.HighlightsBoost))
        {
            return;
        }

        this.toneBoostKernel((int)gpuPixels.Length, gpuPixels, this.Settings.ShadowsBoost, this.Settings.MidtonesBoost, this.Settings.HighlightsBoost);
    }

    private void ApplyLocalContrast(ArrayView1D<Rgb, Stride1D.Dense> gpuPixels, int width, int height, float amount, int radius)
    {
        var strength = Math.Clamp(amount / 100f, -1f, 1f);
        var effectiveRadius = Math.Clamp(radius, 0, 100);
        if (MathF.Abs(strength) <= 1e-6f || effectiveRadius <= 0)
        {
            return;
        }

        if (width <= 0 || height <= 0 || width * height != gpuPixels.Length)
        {
            return;
        }

        var horizontalSums = this.GetLocalContrastBuffer(gpuPixels.Length).View;
        this.localContrastHorizontalKernel((int)gpuPixels.Length, gpuPixels, horizontalSums, width, effectiveRadius);
        this.localContrastVerticalKernel((int)gpuPixels.Length, gpuPixels, horizontalSums, gpuPixels, width, height, strength, effectiveRadius);
    }

    private MemoryBuffer1D<Rgb, Stride1D.Dense> GetBlendBuffer(long length)
    {
        if (this.blendBuffer is null || this.blendBuffer.Length < length)
        {
            this.blendBuffer?.Dispose();
            this.blendBuffer = this.accelerator.Allocate1D<Rgb>(length);
        }

        return this.blendBuffer;
    }

    private MemoryBuffer1D<Rgb, Stride1D.Dense> GetTempBuffer(long length)
    {
        if (this.tempBuffer is null || this.tempBuffer.Length < length)
        {
            this.tempBuffer?.Dispose();
            this.tempBuffer = this.accelerator.Allocate1D<Rgb>(length);
        }

        return this.tempBuffer;
    }

    private MemoryBuffer1D<Rgb, Stride1D.Dense> GetLocalContrastBuffer(long length)
    {
        if (this.localContrastBuffer is null || this.localContrastBuffer.Length < length)
        {
            this.localContrastBuffer?.Dispose();
            this.localContrastBuffer = this.accelerator.Allocate1D<Rgb>(length);
        }

        return this.localContrastBuffer;
    }

    private static void ApplyColorTemperatureKernel(Index1D index, ArrayView1D<Rgb, Stride1D.Dense> pixels, float redScale, float greenScale, float blueScale)
    {
        var px = pixels[index];
        px.Red = XMath.Clamp(px.Red * redScale, 0f, 1f);
        px.Green = XMath.Clamp(px.Green * greenScale, 0f, 1f);
        px.Blue = XMath.Clamp(px.Blue * blueScale, 0f, 1f);
        pixels[index] = px;
    }

    private static void ApplyToneBoostKernel(Index1D index, ArrayView1D<Rgb, Stride1D.Dense> pixels, float shadowsBoost, float midtonesBoost, float highlightsBoost)
    {
        var rgb = pixels[index];
        var lum = XMath.Clamp(rgb.Light(), 0f, 1f);
        var boost = ComputeToneBoost(lum, shadowsBoost, midtonesBoost, highlightsBoost);
        var boostedLum = XMath.Clamp(lum * boost, 0f, 1f);
        var scale = boostedLum / XMath.Max(lum, 1e-6f);

        rgb.Red = XMath.Clamp(rgb.Red * scale, 0f, 1f);
        rgb.Green = XMath.Clamp(rgb.Green * scale, 0f, 1f);
        rgb.Blue = XMath.Clamp(rgb.Blue * scale, 0f, 1f);
        pixels[index] = rgb;
    }

    private static float ComputeToneBoost(float value, float shadowsBoost, float midtonesBoost, float highlightsBoost)
    {
        var shadows = XMath.Clamp((0.5f - value) / 0.5f, 0f, 1f);
        var highlights = XMath.Clamp((value - 0.5f) / 0.5f, 0f, 1f);
        var midtones = XMath.Clamp(1f - (XMath.Abs(value - 0.5f) * 2f), 0f, 1f);
        return (shadows * shadowsBoost) + (midtones * midtonesBoost) + (highlights * highlightsBoost);
    }

    private static void CopyKernel(Index1D index, ArrayView1D<Rgb, Stride1D.Dense> source, ArrayView1D<Rgb, Stride1D.Dense> target)
    {
        target[index] = source[index];
    }

    private static bool ShouldBlend(float transparent)
    {
        return Math.Clamp(transparent, 0f, 100f) > 1e-6f;
    }

    private static void BlendKernel(Index1D index, ArrayView1D<Rgb, Stride1D.Dense> source, ArrayView1D<Rgb, Stride1D.Dense> target, float sourceWeight)
    {
        var resultWeight = 1f - sourceWeight;
        var original = source[index];
        var result = target[index];
        target[index] = new Rgb(
            (result.Red * resultWeight) + (original.Red * sourceWeight),
            (result.Green * resultWeight) + (original.Green * sourceWeight),
            (result.Blue * resultWeight) + (original.Blue * sourceWeight));
    }

    private static void ApplyInputScaleKernel(Index1D index, ArrayView1D<Rgb, Stride1D.Dense> pixels, float scale)
    {
        pixels[index] *= scale;
    }

    private static void ApplyLocalContrastHorizontalKernel(
        Index1D index,
        ArrayView1D<Rgb, Stride1D.Dense> source,
        ArrayView1D<Rgb, Stride1D.Dense> horizontalSums,
        int width,
        int radius)
    {
        var i = (int)index;
        var x = i % width;
        var y = i / width;
        var sumR = 0f;
        var sumG = 0f;
        var sumB = 0f;
        var row = y * width;

        for (var xx = XMath.Max(0, x - radius); xx <= XMath.Min(width - 1, x + radius); xx++)
        {
            var sample = source[row + xx];
            sumR += sample.Red;
            sumG += sample.Green;
            sumB += sample.Blue;
        }

        horizontalSums[index] = new Rgb(sumR, sumG, sumB);
    }

    private static void ApplyLocalContrastVerticalKernel(
        Index1D index,
        ArrayView1D<Rgb, Stride1D.Dense> source,
        ArrayView1D<Rgb, Stride1D.Dense> horizontalSums,
        ArrayView1D<Rgb, Stride1D.Dense> target,
        int width,
        int height,
        float strength,
        int radius)
    {
        var i = (int)index;
        var x = i % width;
        var y = i / width;
        var sumR = 0f;
        var sumG = 0f;
        var sumB = 0f;
        var horizontalSamples = XMath.Min(width - 1, x + radius) - XMath.Max(0, x - radius) + 1;
        var verticalSamples = XMath.Min(height - 1, y + radius) - XMath.Max(0, y - radius) + 1;

        for (var yy = XMath.Max(0, y - radius); yy <= XMath.Min(height - 1, y + radius); yy++)
        {
            var sample = horizontalSums[(yy * width) + x];
            sumR += sample.Red;
            sumG += sample.Green;
            sumB += sample.Blue;
        }

        var p = source[index];
        var invSamples = 1f / (horizontalSamples * verticalSamples);
        var blurR = sumR * invSamples;
        var blurG = sumG * invSamples;
        var blurB = sumB * invSamples;

        p.Red = XMath.Clamp(p.Red + ((p.Red - blurR) * strength), 0f, 1f);
        p.Green = XMath.Clamp(p.Green + ((p.Green - blurG) * strength), 0f, 1f);
        p.Blue = XMath.Clamp(p.Blue + ((p.Blue - blurB) * strength), 0f, 1f);
        target[index] = p;
    }

    protected readonly record struct EffectiveToneMapperSettings(float ExposureEV, float Brightness, float Contrast, float LocalContrast, int LocalContrastRadius, float Saturation, float Gamma);
}
