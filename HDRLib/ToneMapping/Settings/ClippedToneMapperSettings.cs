// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping.Settings;

public abstract class ClippedToneMapperSettings : ToneMapperSettings
{
    public const float NeutralWhiteClip = 1.0f;
    public const float NeutralBlackClip = 0.0f;

    public float WhiteClip { get; set; } = NeutralWhiteClip;
    public float BlackClip { get; set; } = NeutralBlackClip;
}
