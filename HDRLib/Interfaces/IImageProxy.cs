// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Interfaces;

using ILGPU;
using ILGPU.Runtime;

/// <summary>
/// The Image Proxy
/// </summary>
public interface IImageProxy: IDisposable
{
    #region Properties

    /// <summary>
    /// Gets the width.
    /// </summary>
    /// <value>
    /// The width.
    /// </value>
    int Width { get; }

    /// <summary>
    /// Gets the height.
    /// </summary>
    /// <value>
    /// The height.
    /// </value>
    int Height { get; }

    /// <summary>
    /// Gets the exposure time.
    /// </summary>
    /// <value>
    /// The exposure time.
    /// </value>
    double? ExposureTime { get; }

    /// <summary>
    /// Gets the f number.
    /// </summary>
    /// <value>
    /// The f number.
    /// </value>
    double? FNumber { get; }

    /// <summary>
    ///     Gets the shutter speed value.
    /// </summary>
    /// <value>
    ///     The shutter speed value.
    /// </value>
    double? ShutterSpeedValue { get; }

    /// <summary>
    ///     Gets the apperture value.
    /// </summary>
    /// <value>
    ///     The apperture value.
    /// </value>
    double? AppertureValue { get; }

    /// <summary>
    ///     Gets the iso speed rating.
    /// </summary>
    /// <value>
    ///     The iso speed rating.
    /// </value>
    double? IsoSpeedRating { get; }

    /// <summary>
    ///     Gets the exposure bias value.
    /// </summary>
    /// <value>
    ///     The exposure bias value.
    /// </value>
    double? ExposureBiasValue { get; }

    string? CameraMake { get; }

    string? CameraModel { get; }

    #endregion

    #region Methods

    /// <summary>
    /// Saves as JPEG.
    /// </summary>
    /// <param name="fileName">Name of the file.</param>
    void SaveAsJpeg(string fileName);

    /// <summary>
    /// Saves as JPEG.
    /// </summary>
    /// <param name="stream">The stream.</param>
    void SaveAsJpeg(Stream stream);

    /// <summary>
    /// Loads the row.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <returns></returns>
    byte[] LoadRow(int row);

    /// <summary>
    /// Saves the row.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <param name="pixels">The pixels.</param>
    public void SaveRow(int row, byte[] pixels);

    /// <summary>
    /// Gets the pixel.
    /// </summary>
    /// <param name="x">The x.</param>
    /// <param name="y">The y.</param>
    /// <returns></returns>
    byte[] GetPixel(int x, int y);

    /// <summary>
    /// Sets the pixel.
    /// </summary>
    /// <param name="x">The x.</param>
    /// <param name="y">The y.</param>
    /// <param name="pixel">The pixel.</param>
    void SetPixel(int x, int y, byte[] pixel);

    /// <summary>
    /// Loads the specified stream.
    /// </summary>
    /// <param name="stream">The stream.</param>
    void Load(Stream stream);

    /// <summary>
    /// Loads the specified file name.
    /// </summary>
    /// <param name="fileName">Name of the file.</param>
    void Load(string fileName);

    /// <summary>
    /// Creates the specified width.
    /// </summary>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    void Create(int width, int height);

    /// <summary>
    /// Clones the specified rectangle.
    /// </summary>
    /// <param name="rectangle">The rectangle.</param>
    /// <returns></returns>
    public IImageProxy Clone(HDRLib.Align.Rectangle rectangle);

    /// <summary>
    /// Clones this instance.
    /// </summary>
    /// <returns></returns>
    public IImageProxy Clone();

    /// <summary>
    /// Gets the image processor.
    /// </summary>
    /// <value>
    /// The image processor.
    /// </value>
    IImageProcessor ImageProcessor { get; }

    void LoadFullImage(Span<byte> bytes);

    void SaveFullImage(byte[] bytes);

    #endregion
}
