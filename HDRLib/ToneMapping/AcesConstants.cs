// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

/// <summary>
/// Shared constants for ACES fitted tone mapping.
/// </summary>
internal static class AcesConstants
{
    public const float Input00 = 0.59719f;
    public const float Input01 = 0.35458f;
    public const float Input02 = 0.04823f;
    public const float Input10 = 0.07600f;
    public const float Input11 = 0.90834f;
    public const float Input12 = 0.01566f;
    public const float Input20 = 0.02840f;
    public const float Input21 = 0.13383f;
    public const float Input22 = 0.83777f;

    public const float Output00 = 1.60475f;
    public const float Output01 = -0.53108f;
    public const float Output02 = -0.07367f;
    public const float Output10 = -0.10208f;
    public const float Output11 = 1.10813f;
    public const float Output12 = -0.00605f;
    public const float Output20 = -0.00327f;
    public const float Output21 = -0.07276f;
    public const float Output22 = 1.07602f;

    public const float FitA = 0.0245786f;
    public const float FitB = 0.000090537f;
    public const float FitC = 0.983729f;
    public const float FitD = 0.4329510f;
    public const float FitE = 0.238081f;

    public const float ExposureDelta = 1e-4f;
    public const float ExposureEpsilon = 1e-9f;
    public const float ChannelMin = 0.0f;
    public const float ChannelMax = 1.0f;
    public const float ContrastPivot = 0.5f;
}
