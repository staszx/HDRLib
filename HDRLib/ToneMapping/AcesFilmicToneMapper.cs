// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using System.Runtime.Intrinsics.X86;
using Image;
using Settings;

internal sealed class AcesFilmicToneMapper : ToneMapper
{
    #region Constants

    private const float DefaultKey = 0.18f;
    private const float DefaultGamma = 2.2f;
    private readonly AcesFilmicTonemapperSettings settings;

    #endregion

    #region Constructors

    public AcesFilmicToneMapper(AcesFilmicTonemapperSettings settings) : base(settings)
    {
        this.settings = settings;
        this.Key = settings.Key;
        this.Gamma = settings.Gamma;
    }

    #endregion

    #region Properties

    public float Key { get; set; } = DefaultKey;
    public float Gamma { get; set; } = DefaultGamma;

    #endregion

    #region Methods

    protected override bool NormalizesInputRange => false;

    protected override void ApplyInPlace(Image<Rgb> image, EffectiveToneMapperSettings effectiveSettings)
    {
        if (Avx2.IsSupported && !this.settings.AutoAdjustEnabled && !this.ForceToneMappingCore)
        {
            var simd = new AcesFilmicToneMapperSIMD(this.settings);
            this.ApplyUsingSimd(image, simd.ApplyCoreOnlyInPlace);
            return;
        }

        var pixels = image.Pixels;
        var neutralExposureAuto = ToneMapperUtilities.ComputeAutoExposure(pixels, AcesConstants.ExposureDelta, AcesConstants.ExposureEpsilon);
        var exposureManual = MathF.Pow(2.0f, effectiveSettings.ExposureEV);
        var neutralExposure = neutralExposureAuto * exposureManual;
        var exposure = neutralExposure * (this.Key / DefaultKey);

        this.BrightnessContrast(pixels, (int)image.Length, exposure, neutralExposure, effectiveSettings.Brightness, effectiveSettings.Contrast, effectiveSettings.Saturation);
        ToneMapperUtilities.ApplyGamma(pixels, effectiveSettings.Gamma);
    }

    private void BrightnessContrast(Rgb[] pixels, int length, float exposure, float neutralExposure, float brightness, float contrast, float saturation)
    {
        Parallel.For(0, length, i =>
        {
            var p = pixels[i];
            var sourceR = p.Red;
            var sourceG = p.Green;
            var sourceB = p.Blue;
            var r = sourceR * exposure;
            var g = sourceG * exposure;
            var b = sourceB * exposure;
            var neutralR = sourceR * neutralExposure;
            var neutralG = sourceG * neutralExposure;
            var neutralB = sourceB * neutralExposure;

            var acesR = MapAcesChannel(r, g, b, AcesConstants.Input00, AcesConstants.Input01, AcesConstants.Input02);
            var acesG = MapAcesChannel(r, g, b, AcesConstants.Input10, AcesConstants.Input11, AcesConstants.Input12);
            var acesB = MapAcesChannel(r, g, b, AcesConstants.Input20, AcesConstants.Input21, AcesConstants.Input22);
            var neutralAcesR = MapAcesChannel(neutralR, neutralG, neutralB, AcesConstants.Input00, AcesConstants.Input01, AcesConstants.Input02);
            var neutralAcesG = MapAcesChannel(neutralR, neutralG, neutralB, AcesConstants.Input10, AcesConstants.Input11, AcesConstants.Input12);
            var neutralAcesB = MapAcesChannel(neutralR, neutralG, neutralB, AcesConstants.Input20, AcesConstants.Input21, AcesConstants.Input22);

            var mappedR = MapOutputChannel(acesR, acesG, acesB, AcesConstants.Output00, AcesConstants.Output01, AcesConstants.Output02);
            var mappedG = MapOutputChannel(acesR, acesG, acesB, AcesConstants.Output10, AcesConstants.Output11, AcesConstants.Output12);
            var mappedB = MapOutputChannel(acesR, acesG, acesB, AcesConstants.Output20, AcesConstants.Output21, AcesConstants.Output22);
            var neutralMappedR = MapOutputChannel(neutralAcesR, neutralAcesG, neutralAcesB, AcesConstants.Output00, AcesConstants.Output01, AcesConstants.Output02);
            var neutralMappedG = MapOutputChannel(neutralAcesR, neutralAcesG, neutralAcesB, AcesConstants.Output10, AcesConstants.Output11, AcesConstants.Output12);
            var neutralMappedB = MapOutputChannel(neutralAcesR, neutralAcesG, neutralAcesB, AcesConstants.Output20, AcesConstants.Output21, AcesConstants.Output22);

            if (this.ForceToneMappingCore)
            {
                r = mappedR;
                g = mappedG;
                b = mappedB;
            }
            else
            {
                r = sourceR + (mappedR - neutralMappedR);
                g = sourceG + (mappedG - neutralMappedG);
                b = sourceB + (mappedB - neutralMappedB);
            }

            r *= brightness;
            g *= brightness;
            b *= brightness;

            r = ToneMapperUtilities.AdjustContrast(r, contrast);
            g = ToneMapperUtilities.AdjustContrast(g, contrast);
            b = ToneMapperUtilities.AdjustContrast(b, contrast);

            if (MathF.Abs(saturation - 1f) > 1e-6f)
            {
                var lum = (r * 0.2126f) + (g * 0.7152f) + (b * 0.0722f);
                r = lum + ((r - lum) * saturation);
                g = lum + ((g - lum) * saturation);
                b = lum + ((b - lum) * saturation);
            }

            pixels[i].Update(
                Math.Clamp(r, AcesConstants.ChannelMin, AcesConstants.ChannelMax),
                Math.Clamp(g, AcesConstants.ChannelMin, AcesConstants.ChannelMax),
                Math.Clamp(b, AcesConstants.ChannelMin, AcesConstants.ChannelMax));
        });
    }

    private static float MapAcesChannel(float r, float g, float b, float inputR, float inputG, float inputB)
    {
        return ToneMapperUtilities.AcesFitted((r * inputR) + (g * inputG) + (b * inputB));
    }

    private static float MapOutputChannel(float acesR, float acesG, float acesB, float outputR, float outputG, float outputB)
    {
        return (acesR * outputR) + (acesG * outputG) + (acesB * outputB);
    }

    #endregion
}
