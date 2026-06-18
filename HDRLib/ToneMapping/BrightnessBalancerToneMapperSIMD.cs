// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Settings;

internal sealed class BrightnessBalancerToneMapperSIMD : ToneMapperSIMD
{
    private const float Epsilon = 1e-6f;
    private readonly BrightnessBalancerToneMapperSettings settings;

    public BrightnessBalancerToneMapperSIMD(BrightnessBalancerToneMapperSettings settings) : base(settings)
    {
        this.settings = settings;
    }

    protected override bool AppliesToneBoostInternally => true;

    protected override void ApplyCoreInPlace(Vector256<float>[][] pixels, int width, int height)
    {
        var pixelCount = width * height;
        var luminance = ToneMapperSIMDHelper.BuildLuminance(pixels[0], pixels[1], pixels[2], pixelCount);
        var avgLum = LogAverageClamped(luminance);

        var strength = Vector256.Create(GetEffectiveStrength(this.settings));
        var lighting = Vector256.Create(MathF.Max(0f, this.settings.Lighting));
        var brightnessBoost = Vector256.Create(MathF.Max(0f, this.settings.BrightnessBoost) * MathF.Max(this.Settings.Brightness, 0f));
        var blackClipValue = Math.Clamp(this.settings.BlackClip, 0f, 0.99f);
        var whiteClipValue = Math.Clamp(this.settings.WhiteClip, blackClipValue + 1e-3f, 4f);
        var blackClip = Vector256.Create(blackClipValue);
        var invClipRange = Vector256.Create(1f / (whiteClipValue - blackClipValue));
        var exposure = Vector256.Create(MathF.Pow(2f, this.Settings.ExposureEV));
        var contrast = Vector256.Create(MathF.Max(0f, this.Settings.Contrast));
        var saturation = Vector256.Create(SaturationToMultiplier(this.Settings.Saturation));
        var avg = Vector256.Create(avgLum);
        var half = ToneMapperSIMDHelper.Half;

        Parallel.For(0, pixels[0].Length, i =>
        {
            var r = pixels[0][i];
            var g = pixels[1][i];
            var b = pixels[2][i];
            var sourceLum = Avx.Max(
                Avx.Add(
                    Avx.Add(Avx.Multiply(r, ToneMapperSIMDHelper.Rw), Avx.Multiply(g, ToneMapperSIMDHelper.Gw)),
                    Avx.Multiply(b, ToneMapperSIMDHelper.Bw)),
                ToneMapperSIMDHelper.Epsilon);

            var exposedLum = Avx.Multiply(sourceLum, exposure);
            var normalizedLum = Avx.Divide(exposedLum, Avx.Add(ToneMapperSIMDHelper.One, exposedLum));
            var litLum = Avx.Add(avg, Avx.Multiply(Avx.Subtract(normalizedLum, avg), lighting));

            var clippedLum = ToneMapperSIMDHelper.Clamp01(Avx.Multiply(Avx.Subtract(litLum, blackClip), invClipRange));
            clippedLum = ToneMapperSIMDHelper.Clamp01(Avx.Add(Avx.Multiply(Avx.Subtract(clippedLum, half), contrast), half));
            clippedLum = ToneMapperSIMDHelper.Clamp01(Avx.Multiply(clippedLum, brightnessBoost));

            var mappedLum = Avx.Add(sourceLum, Avx.Multiply(Avx.Subtract(clippedLum, sourceLum), strength));
            var scale = Avx.Divide(mappedLum, sourceLum);
            r = Avx.Multiply(r, scale);
            g = Avx.Multiply(g, scale);
            b = Avx.Multiply(b, scale);

            r = Avx.Add(mappedLum, Avx.Multiply(Avx.Subtract(r, mappedLum), saturation));
            g = Avx.Add(mappedLum, Avx.Multiply(Avx.Subtract(g, mappedLum), saturation));
            b = Avx.Add(mappedLum, Avx.Multiply(Avx.Subtract(b, mappedLum), saturation));

            pixels[0][i] = ToneMapperSIMDHelper.Clamp01(r);
            pixels[1][i] = ToneMapperSIMDHelper.Clamp01(g);
            pixels[2][i] = ToneMapperSIMDHelper.Clamp01(b);
        });

        this.ApplyGamma(pixels);
    }

    private void ApplyGamma(Vector256<float>[][] pixels)
    {
        if (MathF.Abs(this.Settings.Gamma - 1f) <= 1e-3f)
        {
            return;
        }

        var invGamma = Vector256.Create(1f / MathF.Max(this.Settings.Gamma, 0.1f));
        Parallel.For(0, pixels[0].Length, i =>
        {
            pixels[0][i] = ToneMapperSIMDHelper.Clamp01(ToneMapperSIMDHelper.Pow(pixels[0][i], invGamma));
            pixels[1][i] = ToneMapperSIMDHelper.Clamp01(ToneMapperSIMDHelper.Pow(pixels[1][i], invGamma));
            pixels[2][i] = ToneMapperSIMDHelper.Clamp01(ToneMapperSIMDHelper.Pow(pixels[2][i], invGamma));
        });
    }

    private static float LogAverageClamped(float[] luminance)
    {
        if (luminance.Length == 0)
        {
            return 0f;
        }

        var sum = 0f;
        for (var i = 0; i < luminance.Length; i++)
        {
            sum += MathF.Log(MathF.Max(luminance[i], Epsilon));
        }

        return MathF.Exp(sum / luminance.Length);
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
}
