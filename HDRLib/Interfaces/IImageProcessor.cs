// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Interfaces;

using System.Runtime.CompilerServices;

/// <summary>
///     The image processor interface
/// </summary>
public interface IImageProcessor
{
    #region Methods

    /// <summary>
    ///     Contrasts the specified value.
    /// </summary>
    /// <param name="value">The value.</param>
    void Contrast(int value);

    /// <summary>
    ///     Brightnesses the specified value.
    /// </summary>
    /// <param name="value">The value.</param>
    void Brightness(int value);

    /// <summary>
    ///     Saturations the specified value.
    /// </summary>
    /// <param name="value">The value.</param>
    void Saturation(int value);

    /// <summary>
    ///     Colors the temperature.
    /// </summary>
    /// <param name="value">The value.</param>
    void ColorTemperature(int value);

    /// <summary>
    /// Resizes the specified width.
    /// </summary>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    void Resize(int width, int height);

    void DrawImage(IImageProxy sourceImage, int x, int y);

    void GaussianBlur(float sigma);

    void Grayscale();

    void BinaryMap(float value);


    #endregion
}