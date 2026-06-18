// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Settings;

internal abstract class ToneMapperSIMD
{
    protected ToneMapperSIMD(ToneMapperSettings settings)
    {
        this.Settings = settings;
    }

    protected ToneMapperSettings Settings { get; }

    public void ApplyInPlace(Vector256<float>[][] pixels, int width, int height)
    {
        this.ApplyCoreInPlace(pixels, width, height);
        if (!this.AppliesToneBoostInternally)
        {
            this.ApplyToneBoostInPlace(pixels);
        }

        DehazeProcessorSIMD.ApplyInPlace(pixels, this.Settings.Dehaze);
    }

    internal void ApplyCoreOnlyInPlace(Vector256<float>[][] pixels, int width, int height)
    {
        this.ApplyCoreInPlace(pixels, width, height);
    }

    protected virtual bool AppliesToneBoostInternally => false;

    protected abstract void ApplyCoreInPlace(Vector256<float>[][] pixels, int width, int height);

    protected static float SaturationToMultiplier(float saturation)
    {
        var value = Math.Clamp(saturation, -100f, 100f);
        return value <= 0f
            ? 1f + (value / 100f)
            : 1f + (value / 50f);
    }

    private void ApplyToneBoostInPlace(Vector256<float>[][] pixels)
    {
        if (ToneBoostProcessor.IsNeutral(this.Settings.ShadowsBoost, this.Settings.MidtonesBoost, this.Settings.HighlightsBoost))
        {
            return;
        }

        var shadowsBoost = Vector256.Create(this.Settings.ShadowsBoost);
        var midtonesBoost = Vector256.Create(this.Settings.MidtonesBoost);
        var highlightsBoost = Vector256.Create(this.Settings.HighlightsBoost);
        var half = Vector256.Create(0.5f);
        var one = Vector256.Create(1f);
        var two = Vector256.Create(2f);
        var eps = Vector256.Create(1e-6f);

        Parallel.For(0, pixels[0].Length, i =>
        {
            var r = pixels[0][i];
            var g = pixels[1][i];
            var b = pixels[2][i];
            var lum = ToneMapperSIMDHelper.Clamp01(Avx.Add(
                Avx.Add(Avx.Multiply(r, ToneMapperSIMDHelper.Rw), Avx.Multiply(g, ToneMapperSIMDHelper.Gw)),
                Avx.Multiply(b, ToneMapperSIMDHelper.Bw)));

            var shadows = ToneMapperSIMDHelper.Clamp01(Avx.Divide(Avx.Subtract(half, lum), half));
            var highlights = ToneMapperSIMDHelper.Clamp01(Avx.Divide(Avx.Subtract(lum, half), half));
            var midtones = ToneMapperSIMDHelper.Clamp01(Avx.Subtract(one, Abs(Avx.Multiply(Avx.Subtract(lum, half), two))));
            var boost = Avx.Add(
                Avx.Add(Avx.Multiply(shadows, shadowsBoost), Avx.Multiply(midtones, midtonesBoost)),
                Avx.Multiply(highlights, highlightsBoost));
            var boostedLum = ToneMapperSIMDHelper.Clamp01(Avx.Multiply(lum, boost));
            var scale = Avx.Divide(boostedLum, Avx.Max(lum, eps));

            pixels[0][i] = ToneMapperSIMDHelper.Clamp01(Avx.Multiply(r, scale));
            pixels[1][i] = ToneMapperSIMDHelper.Clamp01(Avx.Multiply(g, scale));
            pixels[2][i] = ToneMapperSIMDHelper.Clamp01(Avx.Multiply(b, scale));
        });
    }

    private static Vector256<float> Abs(Vector256<float> value)
    {
        return Avx.AndNot(Vector256.Create(-0f), value);
    }
}
