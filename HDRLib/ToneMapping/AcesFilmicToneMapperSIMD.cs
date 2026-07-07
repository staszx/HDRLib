// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Settings;

internal sealed class AcesFilmicToneMapperSIMD : ToneMapperSIMD
{
    private const float DefaultKey = 0.18f;

    private static readonly Vector256<float> Input00 = Vector256.Create(AcesConstants.Input00);
    private static readonly Vector256<float> Input01 = Vector256.Create(AcesConstants.Input01);
    private static readonly Vector256<float> Input02 = Vector256.Create(AcesConstants.Input02);
    private static readonly Vector256<float> Input10 = Vector256.Create(AcesConstants.Input10);
    private static readonly Vector256<float> Input11 = Vector256.Create(AcesConstants.Input11);
    private static readonly Vector256<float> Input12 = Vector256.Create(AcesConstants.Input12);
    private static readonly Vector256<float> Input20 = Vector256.Create(AcesConstants.Input20);
    private static readonly Vector256<float> Input21 = Vector256.Create(AcesConstants.Input21);
    private static readonly Vector256<float> Input22 = Vector256.Create(AcesConstants.Input22);

    private static readonly Vector256<float> Output00 = Vector256.Create(AcesConstants.Output00);
    private static readonly Vector256<float> Output01 = Vector256.Create(AcesConstants.Output01);
    private static readonly Vector256<float> Output02 = Vector256.Create(AcesConstants.Output02);
    private static readonly Vector256<float> Output10 = Vector256.Create(AcesConstants.Output10);
    private static readonly Vector256<float> Output11 = Vector256.Create(AcesConstants.Output11);
    private static readonly Vector256<float> Output12 = Vector256.Create(AcesConstants.Output12);
    private static readonly Vector256<float> Output20 = Vector256.Create(AcesConstants.Output20);
    private static readonly Vector256<float> Output21 = Vector256.Create(AcesConstants.Output21);
    private static readonly Vector256<float> Output22 = Vector256.Create(AcesConstants.Output22);

    private static readonly Vector256<float> FitA = Vector256.Create(AcesConstants.FitA);
    private static readonly Vector256<float> FitB = Vector256.Create(AcesConstants.FitB);
    private static readonly Vector256<float> FitC = Vector256.Create(AcesConstants.FitC);
    private static readonly Vector256<float> FitD = Vector256.Create(AcesConstants.FitD);
    private static readonly Vector256<float> FitE = Vector256.Create(AcesConstants.FitE);

    public AcesFilmicToneMapperSIMD(AcesFilmicTonemapperSettings settings) : base(settings)
    {
        this.Key = settings.Key;
        this.ExposureEV = settings.ExposureEV;
        this.Brightness = settings.Brightness;
        this.Contrast = settings.Contrast;
        this.Saturation = SaturationToMultiplier(settings.Saturation);
        this.Gamma = settings.Gamma;
    }

    public float Key { get; }
    public float ExposureEV { get; }
    public float Brightness { get; }
    public float Contrast { get; }
    public float Saturation { get; }
    public float Gamma { get; }

    protected override bool NormalizesInputRange => false;

    protected override void ApplyCoreInPlace(Vector256<float>[][] pixels, int width, int height)
    {
        var count = width * height;
        var lum = ToneMapperSIMDHelper.BuildLuminance(pixels[0], pixels[1], pixels[2], count);
        var avg = LogAverageExact(lum);
        var neutralExposureAuto = DefaultKey / (avg + AcesConstants.ExposureEpsilon);
        var exposureManual = MathF.Pow(2f, this.ExposureEV);
        var neutralExposure = Vector256.Create(neutralExposureAuto * exposureManual);
        var exposure = Vector256.Create(neutralExposureAuto * (this.Key / DefaultKey) * exposureManual);
        var brightness = Vector256.Create(this.Brightness);
        var contrast = Vector256.Create(this.Contrast);
        var saturation = Vector256.Create(this.Saturation);
        var invGamma = Vector256.Create(1f / this.Gamma);

        Parallel.For(0, pixels[0].Length, i =>
        {
            var sourceR = pixels[0][i];
            var sourceG = pixels[1][i];
            var sourceB = pixels[2][i];
            var r = Avx.Multiply(sourceR, exposure);
            var g = Avx.Multiply(sourceG, exposure);
            var b = Avx.Multiply(sourceB, exposure);
            var neutralR = Avx.Multiply(sourceR, neutralExposure);
            var neutralG = Avx.Multiply(sourceG, neutralExposure);
            var neutralB = Avx.Multiply(sourceB, neutralExposure);

            var acesR = MapAcesChannel(r, g, b, Input00, Input01, Input02);
            var acesG = MapAcesChannel(r, g, b, Input10, Input11, Input12);
            var acesB = MapAcesChannel(r, g, b, Input20, Input21, Input22);
            var neutralAcesR = MapAcesChannel(neutralR, neutralG, neutralB, Input00, Input01, Input02);
            var neutralAcesG = MapAcesChannel(neutralR, neutralG, neutralB, Input10, Input11, Input12);
            var neutralAcesB = MapAcesChannel(neutralR, neutralG, neutralB, Input20, Input21, Input22);

            var mappedR = MapOutputChannel(acesR, acesG, acesB, Output00, Output01, Output02);
            var mappedG = MapOutputChannel(acesR, acesG, acesB, Output10, Output11, Output12);
            var mappedB = MapOutputChannel(acesR, acesG, acesB, Output20, Output21, Output22);
            var neutralMappedR = MapOutputChannel(neutralAcesR, neutralAcesG, neutralAcesB, Output00, Output01, Output02);
            var neutralMappedG = MapOutputChannel(neutralAcesR, neutralAcesG, neutralAcesB, Output10, Output11, Output12);
            var neutralMappedB = MapOutputChannel(neutralAcesR, neutralAcesG, neutralAcesB, Output20, Output21, Output22);

            r = Avx.Add(sourceR, Avx.Subtract(mappedR, neutralMappedR));
            g = Avx.Add(sourceG, Avx.Subtract(mappedG, neutralMappedG));
            b = Avx.Add(sourceB, Avx.Subtract(mappedB, neutralMappedB));

            r = Avx.Multiply(r, brightness);
            g = Avx.Multiply(g, brightness);
            b = Avx.Multiply(b, brightness);

            var pivot = Vector256.Create(AcesConstants.ContrastPivot);
            r = Avx.Add(Avx.Multiply(Avx.Subtract(r, pivot), contrast), pivot);
            g = Avx.Add(Avx.Multiply(Avx.Subtract(g, pivot), contrast), pivot);
            b = Avx.Add(Avx.Multiply(Avx.Subtract(b, pivot), contrast), pivot);

            if (MathF.Abs(this.Saturation - 1f) > 1e-6f)
            {
                var lumOut = Avx.Add(
                    Avx.Add(Avx.Multiply(r, ToneMapperSIMDHelper.Rw), Avx.Multiply(g, ToneMapperSIMDHelper.Gw)),
                    Avx.Multiply(b, ToneMapperSIMDHelper.Bw));
                r = Avx.Add(lumOut, Avx.Multiply(Avx.Subtract(r, lumOut), saturation));
                g = Avx.Add(lumOut, Avx.Multiply(Avx.Subtract(g, lumOut), saturation));
                b = Avx.Add(lumOut, Avx.Multiply(Avx.Subtract(b, lumOut), saturation));
            }

            pixels[0][i] = ToneMapperSIMDHelper.Clamp01(ToneMapperSIMDHelper.Pow(ToneMapperSIMDHelper.Clamp01(r), invGamma));
            pixels[1][i] = ToneMapperSIMDHelper.Clamp01(ToneMapperSIMDHelper.Pow(ToneMapperSIMDHelper.Clamp01(g), invGamma));
            pixels[2][i] = ToneMapperSIMDHelper.Clamp01(ToneMapperSIMDHelper.Pow(ToneMapperSIMDHelper.Clamp01(b), invGamma));
        });
    }

    private static Vector256<float> ApplyAcesFitted(Vector256<float> x)
    {
        x = Avx.Max(ToneMapperSIMDHelper.Zero, x);
        var numerator = Avx.Subtract(Avx.Multiply(x, Avx.Add(x, FitA)), FitB);
        var denominator = Avx.Add(Avx.Multiply(x, Avx.Add(Avx.Multiply(FitC, x), FitD)), FitE);
        return Avx.Max(ToneMapperSIMDHelper.Zero, Avx.Divide(numerator, denominator));
    }

    private static Vector256<float> MapAcesChannel(Vector256<float> r, Vector256<float> g, Vector256<float> b, Vector256<float> inputR, Vector256<float> inputG, Vector256<float> inputB)
    {
        return ApplyAcesFitted(Avx.Add(Avx.Add(Avx.Multiply(r, inputR), Avx.Multiply(g, inputG)), Avx.Multiply(b, inputB)));
    }

    private static Vector256<float> MapOutputChannel(Vector256<float> acesR, Vector256<float> acesG, Vector256<float> acesB, Vector256<float> outputR, Vector256<float> outputG, Vector256<float> outputB)
    {
        return Avx.Add(Avx.Add(Avx.Multiply(acesR, outputR), Avx.Multiply(acesG, outputG)), Avx.Multiply(acesB, outputB));
    }

    private static float LogAverageExact(float[] luminance)
    {
        var sum = 0f;
        for (var i = 0; i < luminance.Length; i++)
        {
            sum += MathF.Log(AcesConstants.ExposureDelta + luminance[i]);
        }

        return MathF.Exp(sum / luminance.Length);
    }
}
