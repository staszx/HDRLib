// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.PostProcessors;

using Image;

internal interface IPostProcessor
{
    #region Methods

    void ApplyInPlace(Image<Rgb> image);

    #endregion
}