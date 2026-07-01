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
        if (Avx2.IsSupported && !this.settings.AutoAdjustEnabled)
        {
            var simd = new AcesFilmicToneMapperSIMD(this.settings);
            this.ApplyUsingSimd(image, simd.ApplyCoreOnlyInPlace);
            return;
        }

        var pixels = image.Pixels;
        var exposureAuto = ToneMapperUtilities.ComputeAutoExposure(pixels, AcesConstants.ExposureDelta, AcesConstants.ExposureEpsilon) * (this.Key / DefaultKey);
        var exposureManual = MathF.Pow(2.0f, effectiveSettings.ExposureEV);
        var exposure = exposureAuto * exposureManual;

        this.BrightnessContrast(pixels, (int)image.Length, exposure, effectiveSettings.Brightness, effectiveSettings.Contrast, effectiveSettings.Saturation);
        ToneMapperUtilities.ApplyGamma(pixels, effectiveSettings.Gamma);
    }

    private void BrightnessContrast(Rgb[] pixels, int length, float exposure, float brightness, float contrast, float saturation)
    {
        Parallel.For(0, length, i =>
        {
            var p = pixels[i];
            var r = p.Red * exposure;
            var g = p.Green * exposure;
            var b = p.Blue * exposure;

            var acesR = (r * AcesConstants.Input00) + (g * AcesConstants.Input01) + (b * AcesConstants.Input02);
            var acesG = (r * AcesConstants.Input10) + (g * AcesConstants.Input11) + (b * AcesConstants.Input12);
            var acesB = (r * AcesConstants.Input20) + (g * AcesConstants.Input21) + (b * AcesConstants.Input22);

            acesR = ToneMapperUtilities.AcesFitted(acesR);
            acesG = ToneMapperUtilities.AcesFitted(acesG);
            acesB = ToneMapperUtilities.AcesFitted(acesB);

            r = ((acesR * AcesConstants.Output00) + (acesG * AcesConstants.Output01) + (acesB * AcesConstants.Output02)) * brightness;
            g = ((acesR * AcesConstants.Output10) + (acesG * AcesConstants.Output11) + (acesB * AcesConstants.Output12)) * brightness;
            b = ((acesR * AcesConstants.Output20) + (acesG * AcesConstants.Output21) + (acesB * AcesConstants.Output22)) * brightness;

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

    #endregion
}
