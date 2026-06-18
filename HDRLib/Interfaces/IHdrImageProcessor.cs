// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Interfaces;

using Image;

public interface IHdrImageProcessor
{
    #region Methods

    void ApplyInPlace(Image<Rgb> image);

    #endregion
}