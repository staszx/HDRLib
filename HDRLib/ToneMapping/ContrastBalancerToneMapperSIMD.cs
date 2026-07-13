// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Settings;

internal sealed class ContrastBalancerToneMapperSIMD : ToneMapperSIMD
{
    private const float Epsilon = 1e-6f;
    private readonly ContrastBalancerToneMapperSettings settings;

    public ContrastBalancerToneMapperSIMD(ContrastBalancerToneMapperSettings settings) : base(settings)
    {
        this.settings = settings;
    }

    protected override bool AppliesToneBoostInternally => false;

    protected override void ApplyCoreInPlace(Vector256<float>[][] pixels, int width, int height)
    {
        var pixelCount = width * height;
        var luminance = ToneMapperSIMDHelper.BuildLuminance(pixels[0], pixels[1], pixels[2], pixelCount);
        var avgLum = LogAverageClamped(luminance);

        var strength = Vector256.Create(GetBalanceStrength(this.settings, this.ForceToneMappingCore));
        var toneCompression = Vector256.Create(MathF.Max(this.settings.ToneCompression, 1e-3f));
        var lightingEffect = Vector256.Create(Math.Max(0f, this.settings.LightingEffect));
        var luminanceScale = Vector256.Create(Math.Max(0f, this.settings.Luminance) * MathF.Pow(2f, this.Settings.ExposureEV));
        var blackClipValue = Math.Clamp(this.settings.BlackClip, 0f, 0.99f);
        var whiteClipValue = Math.Clamp(this.settings.WhiteClip, blackClipValue + 1e-3f, 4f);
        var blackClip = Vector256.Create(blackClipValue);
        var invClipRange = Vector256.Create(1f / (whiteClipValue - blackClipValue));
        var contrast = Vector256.Create(Math.Max(0f, this.Settings.Contrast));
        var saturation = Vector256.Create(SaturationToMultiplier(this.Settings.Saturation));
        var brightness = Vector256.Create(Math.Max(0f, this.Settings.Brightness));
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

            var scaledLum = Avx.Multiply(sourceLum, luminanceScale);
            var normalizedLum = Avx.Divide(scaledLum, Avx.Add(scaledLum, toneCompression));
            var adaptedLum = Avx.Add(avg, Avx.Multiply(Avx.Subtract(normalizedLum, avg), lightingEffect));
            adaptedLum = Avx.Multiply(Avx.Subtract(adaptedLum, blackClip), invClipRange);
            adaptedLum = ToneMapperSIMDHelper.Clamp01(Avx.Add(Avx.Multiply(Avx.Subtract(adaptedLum, half), contrast), half));
            adaptedLum = ToneMapperSIMDHelper.Clamp01(Avx.Multiply(adaptedLum, brightness));

            var mappedLum = Avx.Add(sourceLum, Avx.Multiply(Avx.Subtract(adaptedLum, sourceLum), strength));
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

    private static float GetBalanceStrength(ContrastBalancerToneMapperSettings settings, bool forceToneMappingCore)
    {
        return forceToneMappingCore || HasActiveBalanceControls(settings)
            ? Math.Clamp(settings.Strength, 0f, 1f)
            : 0f;
    }

    private static bool HasActiveBalanceControls(ContrastBalancerToneMapperSettings settings)
    {
        return MathF.Abs(settings.ToneCompression - 1f) > Epsilon ||
               MathF.Abs(settings.LightingEffect - 1f) > Epsilon ||
               MathF.Abs(settings.Luminance - 1f) > Epsilon ||
               MathF.Abs(settings.WhiteClip - ClippedToneMapperSettings.NeutralWhiteClip) > Epsilon ||
               MathF.Abs(settings.BlackClip - ClippedToneMapperSettings.NeutralBlackClip) > Epsilon ||
               MathF.Abs(settings.ExposureEV) > Epsilon ||
               MathF.Abs(settings.Brightness - 1f) > Epsilon ||
               MathF.Abs(settings.Contrast - 1f) > Epsilon;
    }

}
