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
}
