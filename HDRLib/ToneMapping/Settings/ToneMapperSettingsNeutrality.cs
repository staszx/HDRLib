// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping.Settings;

public static class ToneMapperSettingsNeutrality
{
    private const float Epsilon = 1e-5f;

    public static bool IsNeutral(this ToneMapperSettings settings)
    {
        if (settings.AutoAdjustEnabled ||
            MathF.Abs(settings.ExposureEV) > Epsilon ||
            MathF.Abs(settings.Brightness - 1f) > Epsilon ||
            MathF.Abs(settings.Contrast - 1f) > Epsilon ||
            MathF.Abs(settings.ShadowsBoost - 1f) > Epsilon ||
            MathF.Abs(settings.MidtonesBoost - 1f) > Epsilon ||
            MathF.Abs(settings.HighlightsBoost - 1f) > Epsilon ||
            MathF.Abs(settings.Dehaze) > Epsilon ||
            MathF.Abs(settings.LocalContrast) > Epsilon ||
            MathF.Abs(settings.Saturation) > Epsilon ||
            settings.GetSaturationColorRanges().Length != 0 ||
            MathF.Abs(settings.Gamma - 1f) > Epsilon ||
            !IsNeutralColorTemperature(settings.ColorTemperature) ||
            settings.WhiteBalanceReferenceType != WhiteBalanceReferenceType.None ||
            !settings.PostProcess.IsNeutral(Epsilon))
        {
            return false;
        }

        return settings switch
        {
            AcesFilmicTonemapperSettings aces => IsCoreNeutral(aces),
            NaturalToneMapperSettings compressor => IsCoreNeutral(compressor),
            ContrastBalancerToneMapperSettings contrastOptimizer => IsCoreNeutral(contrastOptimizer),
            BrightnessBalancerToneMapperSettings toneBalancer => IsCoreNeutral(toneBalancer),
            _ => true
        };
    }

    public static bool IsCoreNeutral(this ToneMapperSettings settings)
    {
        return settings switch
        {
            AcesFilmicTonemapperSettings aces => IsCoreNeutral(aces),
            NaturalToneMapperSettings compressor => IsCoreNeutral(compressor),
            ContrastBalancerToneMapperSettings contrastOptimizer => IsCoreNeutral(contrastOptimizer),
            BrightnessBalancerToneMapperSettings toneBalancer => IsCoreNeutral(toneBalancer),
            _ => true
        };
    }

    public static T MakeNeutral<T>(this T settings)
        where T : ToneMapperSettings
    {
        settings.AutoAdjustEnabled = false;
        settings.ExposureEV = 0f;
        settings.Brightness = 1f;
        settings.Contrast = 1f;
        settings.ShadowsBoost = 1f;
        settings.MidtonesBoost = 1f;
        settings.HighlightsBoost = 1f;
        settings.Dehaze = 0f;
        settings.LocalContrast = 0f;
        settings.LocalContrastRadius = 1;
        settings.Transparent = 0f;
        settings.Saturation = 0f;
        settings.SaturateExcludes = [];
        settings.SaturationFilters = [];
        settings.SkinFilter = SaturationFilterPresets.CreateSkinFilter();
        settings.GrayColorFilter = SaturationFilterPresets.CreateGrayFilter();
        settings.Gamma = 1f;
        settings.ColorTemperature = 6500f;
        settings.WhiteBalanceReferenceType = WhiteBalanceReferenceType.None;
        settings.PostProcess = new();

        switch (settings)
        {
            case AcesFilmicTonemapperSettings aces:
                aces.Key = 0.18f;
                break;
            case NaturalToneMapperSettings compressor:
                compressor.TargetGray = 0.24f;
                compressor.WhitePointPercentile = 1f;
                compressor.OutputMidGray = 0.25f;
                compressor.AutoBrightnessCompensation = false;
                compressor.BypassToneCompressionForLdr = true;
                compressor.LdrBypassWhitePointThreshold = float.MaxValue;
                compressor.TonalRangeCompression = 4f;
                break;
            case ContrastBalancerToneMapperSettings contrastOptimizer:
                contrastOptimizer.Strength = 0f;
                contrastOptimizer.ToneCompression = 1f;
                contrastOptimizer.LightingEffect = 1f;
                contrastOptimizer.Luminance = 1f;
                contrastOptimizer.WhiteClip = 1f;
                contrastOptimizer.BlackClip = 0f;
                break;
            case BrightnessBalancerToneMapperSettings toneBalancer:
                toneBalancer.Strength = 0f;
                toneBalancer.Lighting = 1f;
                toneBalancer.BrightnessBoost = 1f;
                toneBalancer.WhiteClip = 1f;
                toneBalancer.BlackClip = 0f;
                break;
        }

        return settings;
    }

    private static bool IsCoreNeutral(AcesFilmicTonemapperSettings settings)
    {
        return MathF.Abs(settings.Key - 0.18f) <= Epsilon;
    }

    private static bool IsNeutralColorTemperature(float colorTemperature)
    {
        return MathF.Abs(colorTemperature) <= Epsilon ||
               MathF.Abs(colorTemperature - 6500f) <= Epsilon;
    }

    private static bool IsCoreNeutral(NaturalToneMapperSettings settings)
    {
        return !settings.AutoBrightnessCompensation &&
               settings.BypassToneCompressionForLdr;
    }

    private static bool IsCoreNeutral(ContrastBalancerToneMapperSettings settings)
    {
        return MathF.Abs(settings.Strength) <= Epsilon &&
               MathF.Abs(settings.ToneCompression - 1f) <= Epsilon &&
               MathF.Abs(settings.LightingEffect - 1f) <= Epsilon &&
               MathF.Abs(settings.Luminance - 1f) <= Epsilon &&
               IsNeutralClip(settings);
    }

    private static bool IsCoreNeutral(BrightnessBalancerToneMapperSettings settings)
    {
        return MathF.Abs(settings.Strength) <= Epsilon &&
               MathF.Abs(settings.Lighting - 1f) <= Epsilon &&
               MathF.Abs(settings.BrightnessBoost - 1f) <= Epsilon &&
               IsNeutralClip(settings);
    }

    private static bool IsNeutralClip(ClippedToneMapperSettings settings)
    {
        return MathF.Abs(settings.WhiteClip - ClippedToneMapperSettings.NeutralWhiteClip) <= Epsilon &&
               MathF.Abs(settings.BlackClip - ClippedToneMapperSettings.NeutralBlackClip) <= Epsilon;
    }
}
