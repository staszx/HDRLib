// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using System.Runtime.Intrinsics.X86;
using Image;
using Settings;

internal sealed class BrightnessBalancerToneMapper : ToneMapper
{
    private const float Epsilon = 1e-6f;
    private readonly BrightnessBalancerToneMapperSettings settings;

    public BrightnessBalancerToneMapper(BrightnessBalancerToneMapperSettings settings) : base(settings)
    {
        this.settings = settings;
    }

    protected override unsafe void ApplyInPlace(Image<Rgb> image, EffectiveToneMapperSettings effectiveSettings)
    {
        if (Avx2.IsSupported && !this.settings.AutoAdjustEnabled && !this.ForceToneMappingCore)
        {
            var simd = new BrightnessBalancerToneMapperSIMD(this.settings);
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
        var strength = Math.Clamp(this.settings.Strength, 0f, 1f);
        var lighting = MathF.Max(0f, this.settings.Lighting);
        var brightnessBoost = MathF.Max(0f, this.settings.BrightnessBoost) * MathF.Max(effectiveSettings.Brightness, 0f);
        var hasBalanceControls = this.ForceToneMappingCore || HasActiveBalanceControls(this.settings);
        var blackClip = Math.Clamp(this.settings.BlackClip, 0f, 0.99f);
        var whiteClip = Math.Clamp(this.settings.WhiteClip, blackClip + 1e-3f, 4f);
        var invClipRange = 1f / (whiteClip - blackClip);
        var exposure = MathF.Pow(2f, effectiveSettings.ExposureEV);
        var contrast = MathF.Max(0f, effectiveSettings.Contrast);
        var saturation = MathF.Max(0f, effectiveSettings.Saturation);

        Parallel.For(0, count, i =>
        {
            var rgb = pixels[i];
            var sourceLum = MathF.Max(rgb.Light(), Epsilon);
            var exposedLum = sourceLum * exposure;
            var normalizedLum = exposedLum / (1f + exposedLum);
            var litLum = avgLum + ((normalizedLum - avgLum) * lighting);

            var balancedLum = Math.Clamp((litLum - blackClip) * invClipRange, 0f, 1f);
            balancedLum = Math.Clamp(((balancedLum - 0.5f) * contrast) + 0.5f, 0f, 1f);

            var clippedLum = hasBalanceControls
                ? Math.Clamp(balancedLum * brightnessBoost, 0f, 1f)
                : Math.Clamp(sourceLum * brightnessBoost, 0f, 1f);

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
        });

        ApplyGamma(pixels, count, effectiveSettings.Gamma);
    }

    private static bool HasActiveBalanceControls(BrightnessBalancerToneMapperSettings settings)
    {
        return MathF.Abs(settings.Lighting - 1f) > Epsilon ||
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
