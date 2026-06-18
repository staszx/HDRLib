// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using HDRLib.Gpu;
using HDRLib.Image;
using HDRLib.ToneMapping.Settings;
using ILGPU;
using ILGPU.Runtime;

internal sealed class BrightnessBalancerToneMapperGpu : ToneMapperGpu
{
    private const float Epsilon = 1e-6f;
    private readonly BrightnessBalancerToneMapperSettings settings;

    public BrightnessBalancerToneMapperGpu(GpuContext context, BrightnessBalancerToneMapperSettings settings) : base(context, settings)
    {
        this.settings = settings;
    }

    protected override void ApplyInPlace(ArrayView1D<Rgb, Stride1D.Dense> gpuPixels, EffectiveToneMapperSettings effectiveSettings)
    {
        var pixelCount = (int)gpuPixels.Length;
        var pixels = gpuPixels.GetAsArray1D();
        var logSum = 0.0f;
        for (var i = 0; i < pixelCount; i++)
        {
            var lum = MathF.Max(pixels[i].Light(), Epsilon);
            logSum += MathF.Log(lum);
        }

        var avgLum = MathF.Exp(logSum / pixelCount);
        var strength = GetEffectiveStrength(this.settings);
        var lighting = MathF.Max(0f, this.settings.Lighting);
        var brightnessBoost = MathF.Max(0f, this.settings.BrightnessBoost) * MathF.Max(effectiveSettings.Brightness, 0f);
        var blackClip = Math.Clamp(this.settings.BlackClip, 0f, 0.99f);
        var whiteClip = Math.Clamp(this.settings.WhiteClip, blackClip + 1e-3f, 4f);
        var invClipRange = 1f / (whiteClip - blackClip);
        var exposure = MathF.Pow(2f, effectiveSettings.ExposureEV);
        var contrast = MathF.Max(0f, effectiveSettings.Contrast);
        var saturation = MathF.Max(0f, effectiveSettings.Saturation);

        for (var i = 0; i < pixelCount; i++)
        {
            var rgb = pixels[i];
            var sourceLum = MathF.Max(rgb.Light(), Epsilon);
            var exposedLum = sourceLum * exposure;
            var normalizedLum = exposedLum / (1f + exposedLum);
            var litLum = avgLum + ((normalizedLum - avgLum) * lighting);

            var clippedLum = Math.Clamp((litLum - blackClip) * invClipRange, 0f, 1f);
            clippedLum = Math.Clamp(((clippedLum - 0.5f) * contrast) + 0.5f, 0f, 1f);
            clippedLum = Math.Clamp(clippedLum * brightnessBoost, 0f, 1f);

            var mappedLum = sourceLum + ((clippedLum - sourceLum) * strength);
            var scale = mappedLum / sourceLum;
            rgb *= scale;

            rgb.Red = mappedLum + ((rgb.Red - mappedLum) * saturation);
            rgb.Green = mappedLum + ((rgb.Green - mappedLum) * saturation);
            rgb.Blue = mappedLum + ((rgb.Blue - mappedLum) * saturation);

            rgb.Red = Math.Clamp(rgb.Red, 0f, 1f);
            rgb.Green = Math.Clamp(rgb.Green, 0f, 1f);
            rgb.Blue = Math.Clamp(rgb.Blue, 0f, 1f);
            pixels[i] = rgb;
        }

        ApplyGamma(pixels, effectiveSettings.Gamma);
        gpuPixels.CopyFromCPU(pixels);
    }

    private static float GetEffectiveStrength(BrightnessBalancerToneMapperSettings settings)
    {
        var strength = Math.Clamp(settings.Strength, 0f, 1f);
        if (strength > Epsilon)
        {
            return strength;
        }

        return HasActiveToneControls(settings) ? 1f : 0f;
    }

    private static bool HasActiveToneControls(BrightnessBalancerToneMapperSettings settings)
    {
        return MathF.Abs(settings.Lighting - 1f) > Epsilon ||
               MathF.Abs(settings.BrightnessBoost - 1f) > Epsilon ||
               MathF.Abs(settings.WhiteClip - ClippedToneMapperSettings.NeutralWhiteClip) > Epsilon ||
               MathF.Abs(settings.BlackClip - ClippedToneMapperSettings.NeutralBlackClip) > Epsilon;
    }

    private static void ApplyGamma(Rgb[] pixels, float gamma)
    {
        if (MathF.Abs(gamma - 1f) <= 1e-3f)
        {
            return;
        }

        var invGamma = 1f / MathF.Max(gamma, 0.1f);
        for (var i = 0; i < pixels.Length; i++)
        {
            var rgb = pixels[i];
            rgb.Red = Math.Clamp(MathF.Pow(rgb.Red, invGamma), 0f, 1f);
            rgb.Green = Math.Clamp(MathF.Pow(rgb.Green, invGamma), 0f, 1f);
            rgb.Blue = Math.Clamp(MathF.Pow(rgb.Blue, invGamma), 0f, 1f);
            pixels[i] = rgb;
        }
    }
}
