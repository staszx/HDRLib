// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.PostProcessors;

public sealed class PostProcessSettings
{
    #region Properties

    public float Exposure { get; set; } = 0.0f;
    public float Brightness { get; set; } = 1.0f;
    public float Shadows { get; set; } = 1.0f;
    public float Midtones { get; set; } = 1.0f;
    public float Highlights { get; set; } = 1.0f;
    public float Contrast { get; set; } = 1.0f;
    public float Vibrance { get; set; } = 1.0f;

    #endregion

    #region Methods

    public bool IsNeutral(float epsilon = 1e-6f)
    {
        return MathF.Abs(this.Exposure) <= epsilon &&
               MathF.Abs(this.Brightness - 1f) <= epsilon &&
               MathF.Abs(this.Shadows - 1f) <= epsilon &&
               MathF.Abs(this.Midtones - 1f) <= epsilon &&
               MathF.Abs(this.Highlights - 1f) <= epsilon &&
               MathF.Abs(this.Contrast - 1f) <= epsilon &&
               MathF.Abs(this.Vibrance - 1f) <= epsilon;
    }

    public PostProcessSettings Combine(PostProcessSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new PostProcessSettings
        {
            Exposure = this.Exposure + other.Exposure,
            Brightness = this.Brightness * other.Brightness,
            Shadows = this.Shadows * other.Shadows,
            Midtones = this.Midtones * other.Midtones,
            Highlights = this.Highlights * other.Highlights,
            Contrast = this.Contrast * other.Contrast,
            Vibrance = this.Vibrance * other.Vibrance
        };
    }

    #endregion
}
