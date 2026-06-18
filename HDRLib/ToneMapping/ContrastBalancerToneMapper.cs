// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using System.Runtime.Intrinsics.X86;
using Image;
using Settings;

internal sealed class ContrastBalancerToneMapper : ToneMapper
{
    private const float Epsilon = 1e-6f;
    private readonly ContrastBalancerToneMapperSettings settings;

    public ContrastBalancerToneMapper(ContrastBalancerToneMapperSettings settings) : base(settings)
    {
        this.settings = settings;
    }

    protected override unsafe void ApplyInPlace(Image<Rgb> image, EffectiveToneMapperSettings effectiveSettings)
    {
        if (Avx2.IsSupported && this.settings.AutoAdjustType != AutoAdjustType.Simple)
        {
            var simd = new ContrastBalancerToneMapperSIMD(this.settings);
            this.ApplyUsingSimd(image, simd.ApplyCoreOnlyInPlace);
            return;
        }

        var count = image.Length;
        using var handle = new PinnedArray<Rgb>(image.Pixels);
        var pixels = handle.Pointer;

        var logSum = 0.0f;
        for (var i = 0; i < count; i++)
        {
            var lum = MathF.Max(pixels[i].Light(), Epsilon);
            logSum += MathF.Log(lum);
        }

        var avgLum = MathF.Exp(logSum / count);
        var strength = GetEffectiveStrength(this.settings);
        var toneCompression = MathF.Max(this.settings.ToneCompression, 1e-3f);
        var lightingEffect = Math.Max(0f, this.settings.LightingEffect);
        var luminanceScale = Math.Max(0f, this.settings.Luminance) * MathF.Pow(2f, effectiveSettings.ExposureEV);
        var blackClip = Math.Clamp(this.settings.BlackClip, 0f, 0.99f);
        var whiteClip = Math.Clamp(this.settings.WhiteClip, blackClip + 1e-3f, 4f);
        var invClipRange = 1f / (whiteClip - blackClip);
        var contrast = Math.Max(0f, effectiveSettings.Contrast);
        var saturation = Math.Max(0f, effectiveSettings.Saturation);
        var brightness = Math.Max(0f, effectiveSettings.Brightness);

        Parallel.For(0, count, i =>
        {
            var rgb = pixels[i];
            var sourceLum = MathF.Max(rgb.Light(), Epsilon);
            var normalizedLum = (sourceLum * luminanceScale) / (sourceLum * luminanceScale + toneCompression);
            var adaptedLum = avgLum + ((normalizedLum - avgLum) * lightingEffect);
            adaptedLum = ((adaptedLum - blackClip) * invClipRange);
            adaptedLum = Math.Clamp(((adaptedLum - 0.5f) * contrast) + 0.5f, 0f, 1f);
            adaptedLum = Math.Clamp(adaptedLum * brightness, 0f, 1f);
            var mappedLum = sourceLum + ((adaptedLum - sourceLum) * strength);

            var scale = mappedLum / sourceLum;
            rgb *= scale;
            rgb.Red = mappedLum + ((rgb.Red - mappedLum) * saturation);
            rgb.Green = mappedLum + ((rgb.Green - mappedLum) * saturation);
            rgb.Blue = mappedLum + ((rgb.Blue - mappedLum) * saturation);

            rgb.Red = Math.Clamp(rgb.Red, 0f, 1f);
            rgb.Green = Math.Clamp(rgb.Green, 0f, 1f);
            rgb.Blue = Math.Clamp(rgb.Blue, 0f, 1f);
            pixels[i] = rgb;
        });

        ApplyGamma(pixels, count, effectiveSettings.Gamma);
    }

    private static float GetEffectiveStrength(ContrastBalancerToneMapperSettings settings)
    {
        var strength = Math.Clamp(settings.Strength, 0f, 1f);
        if (strength > Epsilon)
        {
            return strength;
        }

        return HasActiveToneControls(settings) ? 1f : 0f;
    }

    private static bool HasActiveToneControls(ContrastBalancerToneMapperSettings settings)
    {
        return MathF.Abs(settings.ToneCompression - 1f) > Epsilon ||
               MathF.Abs(settings.LightingEffect - 1f) > Epsilon ||
               MathF.Abs(settings.Luminance - 1f) > Epsilon ||
               MathF.Abs(settings.WhiteClip - ClippedToneMapperSettings.NeutralWhiteClip) > Epsilon ||
               MathF.Abs(settings.BlackClip - ClippedToneMapperSettings.NeutralBlackClip) > Epsilon;
    }

    private static unsafe void ApplyGamma(Rgb* pixels, long count, float gamma)
    {
        if (MathF.Abs(gamma - 1f) <= 1e-3f)
        {
            return;
        }

        var invGamma = 1f / MathF.Max(gamma, 0.1f);
        Parallel.For(0, count, i =>
        {
            var rgb = pixels[i];
            rgb.Red = Math.Clamp(MathF.Pow(rgb.Red, invGamma), 0f, 1f);
            rgb.Green = Math.Clamp(MathF.Pow(rgb.Green, invGamma), 0f, 1f);
            rgb.Blue = Math.Clamp(MathF.Pow(rgb.Blue, invGamma), 0f, 1f);
            pixels[i] = rgb;
        });
    }
}
