// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.PixelProvider.ImageSharp;

using System.Runtime.InteropServices;
using ILGPU;
using ILGPU.Runtime;
using Interfaces;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

/// <summary>
///     The Image sharp proxy image
/// </summary>
/// <seealso cref="Interfaces.IImageProxy" />
public class ImageSharpProxy : Disposable, IImageProxy
{
    #region Fields

    /// <summary>
    ///     The image
    /// </summary>
    private Image<Rgb24> image;

    #endregion

    #region Properties

    /// <summary>
    ///     Gets the image.
    /// </summary>
    /// <value>
    ///     The image.
    /// </value>
    internal Image<Rgb24> DestImage => this.image;

    #endregion

    #region Methods

    public void Create(IImageProxy image, Rectangle rectangle)
    {
        var im = ((ImageSharpProxy)image).image;
        this.image = new Image<Rgb24>(image.Width, image.Height);
        this.image.Mutate(i => i.DrawImage(im, new Point(rectangle.Left, rectangle.Top), new Rectangle(0, 0, rectangle.Width, rectangle.Height),
            new GraphicsOptions()));
        this.Initialization();
    }

    protected override void ResourceDispose()
    {
        this.image?.Dispose();
    }

    /// <summary>
    ///     Gets the exposure time.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <returns></returns>
    private static double? GetExposureTime(Image<Rgb24> image)
    {
        var val = (Rational?)GetExifValue(image, ExifTag.ExposureTime);
        return RationalToDouble(val);
    }

    private static string? GetCameraMake(Image<Rgb24> image)
    {
        return (string?)GetExifValue(image, ExifTag.Make);
    }

    private static string? GetCameraModel(Image<Rgb24> image)
    {
        return (string?)GetExifValue(image, ExifTag.Model);
    }

    /// <summary>
    ///     Gets the fnumber.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <returns></returns>
    private static double? GetFnumber(Image<Rgb24> image)
    {
        var val = (Rational?)GetExifValue(image, ExifTag.FNumber);
        return RationalToDouble(val);
    }

    /// <summary>
    ///     Gets the shutter speed value.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <returns></returns>
    private static double? GetShutterSpeedValue(Image<Rgb24> image)
    {
        var val = (SignedRational?)GetExifValue(image, ExifTag.ShutterSpeedValue);
        return RationalToDouble(val);
    }

    /// <summary>
    ///     Gets the apperture value.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <returns></returns>
    private static double? GetAppertureValue(Image<Rgb24> image)
    {
        var val = (Rational?)GetExifValue(image, ExifTag.ApertureValue);
        return RationalToDouble(val);
    }

    /// <summary>
    ///     Gets the iso speed rating.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <returns></returns>
    private static double? GetIsoSpeedRating(Image<Rgb24> image)
    {
        var val = (ushort[]?)GetExifValue(image, ExifTag.ISOSpeedRatings);
        if (val == null || val.Length == 0)
        {
            return null;
        }

        return val[0];
    }

    /// <summary>
    ///     Gets the exposure bias value.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <returns></returns>
    private static double? GetExposureBiasValue(Image<Rgb24> image)
    {
        var val = (SignedRational?)GetExifValue(image, ExifTag.ExposureBiasValue);
        return RationalToDouble(val);
    }

    private static object? GetExifValue(Image image, ExifTag tag)
    {
        var value = image.Metadata.ExifProfile?.Values.FirstOrDefault(v => v.Tag == tag);
        return value?.GetValue();
    }

    /// <summary>
    ///     Rationals to double.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns></returns>
    private static double? RationalToDouble(Rational? value)
    {
        if (value == null)
        {
            return null;
        }

        return (double)value.Value.Numerator / value.Value.Denominator;
    }

    /// <summary>
    ///     Rationals to double.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns></returns>
    private static double? RationalToDouble(SignedRational? value)
    {
        if (value == null)
        {
            return null;
        }

        return (double)value.Value.Numerator / value.Value.Denominator;
    }

    /// <summary>
    ///     Loads the ex if data.
    /// </summary>
    /// <returns></returns>
    private void LoadExIfData()
    {
        this.ExposureTime = GetExposureTime(this.image);
        this.AppertureValue = GetAppertureValue(this.image);
        this.ExposureBiasValue = GetExposureBiasValue(this.image);
        this.FNumber = GetFnumber(this.image);
        this.IsoSpeedRating = GetIsoSpeedRating(this.image);
        this.ShutterSpeedValue = GetShutterSpeedValue(this.image);
        this.CameraMake = GetCameraMake(this.image);
        this.CameraModel = GetCameraModel(this.image);
    }

    /// <summary>
    ///     Initializations this instance.
    /// </summary>
    /// <returns></returns>
    private void Initialization()
    {
        this.LoadExIfData();
        this.ImageProcessor = new ImageSharpProcessor(this.image);
    }

    #endregion

    /// <summary>
    ///     Gets the f number.
    /// </summary>
    /// <value>
    ///     The f number.
    /// </value>
    public double? FNumber { get; private set; }

    /// <summary>
    ///     Gets the shutter speed value.
    /// </summary>
    /// <value>
    ///     The shutter speed value.
    /// </value>
    public double? ShutterSpeedValue { get; private set; }

    /// <summary>
    ///     Gets the apperture value.
    /// </summary>
    /// <value>
    ///     The apperture value.
    /// </value>
    public double? AppertureValue { get; private set; }

    /// <summary>
    ///     Gets the iso speed rating.
    /// </summary>
    /// <value>
    ///     The iso speed rating.
    /// </value>
    public double? IsoSpeedRating { get; private set; }

    /// <summary>
    ///     Gets the exposure bias value.
    /// </summary>
    /// <value>
    ///     The exposure bias value.
    /// </value>
    public double? ExposureBiasValue { get; private set; }

    public string? CameraMake { get; private set; }

    public string? CameraModel { get; private set; }

    /// <summary>
    ///     Gets the image processor.
    /// </summary>
    /// <value>
    ///     The image processor.
    /// </value>
    public IImageProcessor ImageProcessor { get; private set; }

    /// <summary>
    ///     Gets the width.
    /// </summary>
    /// <value>
    ///     The width.
    /// </value>
    public int Width => this.image.Width;

    /// <summary>
    ///     Gets the height.
    /// </summary>
    /// <value>
    ///     The height.
    /// </value>
    public int Height => this.image.Height;

    /// <summary>
    ///     Gets the exposure time.
    /// </summary>
    /// <value>
    ///     The exposure time.
    /// </value>
    public double? ExposureTime { get; private set; }

    /// <summary>
    ///     Loads the row.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <returns></returns>
    public byte[] LoadRow(int row)
    {

            byte[] res = null;
            this.image.ProcessPixelRows(accessor =>
            {
                var result = accessor.GetRowSpan(row);
                var bytes = MemoryMarshal.Cast<Rgb24, byte>(result);
                res = bytes.ToArray();
            });

            return res;
    }

    public void LoadFullImage(Span<byte> bytes)
    {
        this.image.CopyPixelDataTo(bytes);
    }

    /// <summary>
    ///     Saves the full image from a byte array.
    /// </summary>
    /// <param name="bytes">Byte array containing pixel data in RGB24 order.</param>
    public void SaveFullImage(byte[] bytes)
    {
        // Ensure that the provided buffer length matches the image size.
        int expectedLength = this.image.Width * this.image.Height * 3;
        if (bytes == null)
            throw new ArgumentNullException(nameof(bytes));
        if (bytes.Length != expectedLength)
            throw new ArgumentException($"Byte array length must be exactly {expectedLength} for the current image dimensions.", nameof(bytes));

        int offset = 0;
        this.image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var rowSpan = accessor.GetRowSpan(y);
                var rowBytes = MemoryMarshal.Cast<Rgb24, byte>(rowSpan);
                int rowLength = rowBytes.Length;
                // Copy the corresponding slice from the source array into the row.
                bytes.AsSpan(offset, rowLength).CopyTo(rowBytes);
                offset += rowLength;
            }
        });
    }

    /// <summary>
    ///     Saves the row.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <param name="pixels">The pixels.</param>
    public void SaveRow(int row, byte[] pixels)
    {
        this.image.ProcessPixelRows(accessor =>
        {
            var pixelRow = accessor.GetRowSpan(row);
            var length = pixelRow.Length;
            var idx = 0;
            for (var i = 0; i < length; i++)
            {
                pixelRow[i].R = pixels[idx++];
                pixelRow[i].G = pixels[idx++];
                pixelRow[i].B = pixels[idx++];
            }
        });
    }

    /// <summary>
    ///     Loads the specified stream.
    /// </summary>
    /// <param name="stream">The stream.</param>
    public void Load(Stream stream)
    {
        this.image = Image.Load<Rgb24>(stream);
        this.Initialization();
    }

    /// <summary>
    ///     Loads the specified file name.
    /// </summary>
    /// <param name="fileName">Name of the file.</param>
    public void Load(string fileName)
    {
        this.image = Image.Load<Rgb24>(fileName);
        this.Initialization();
    }

    /// <summary>
    ///     Creates the specified width.
    /// </summary>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public void Create(int width, int height)
    {
        this.image = new Image<Rgb24>(width, height);
        this.Initialization();
    }

    /// <summary>
    ///     Clones the specified image.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="rectangle">The rectangle.</param>
    /// <returns></returns>
    public IImageProxy Clone(Align.Rectangle rectangle)
    {
        var result = new ImageSharpProxy();
        var im = this.image;
        result.image = new Image<Rgb24>(this.image.Width, this.image.Height);
        result.image.Metadata.ExifProfile = this.image.Metadata.ExifProfile;
        result.image.Mutate(i => i.DrawImage(im, new Point(rectangle.Left, rectangle.Top), new Rectangle(0, 0, rectangle.Width, rectangle.Height),
            new GraphicsOptions()));
        result.Initialization();
        return result;
    }

    /// <summary>
    ///     Clones this instance.
    /// </summary>
    /// <returns></returns>
    public IImageProxy Clone()
    {
        var result = Activator.CreateInstance(this.GetType()) as ImageSharpProxy;
        result.image = this.image.Clone();
        result.Initialization();
        return result;
    }

    /// <summary>
    ///     Gets the pixel.
    /// </summary>
    /// <param name="x">The x.</param>
    /// <param name="y">The y.</param>
    /// <returns></returns>
    public byte[] GetPixel(int x, int y)
    {
        var pixel = this.image[x, y];
        return new[] { pixel.R, pixel.G, pixel.B };
    }

    public void SetPixel(int x, int y, byte[] pixel)
    {
        this.image[x, y] = new Rgb24(pixel[0], pixel[1], pixel[2]);
    }

    public void CopyMetadataFrom(ImageSharpProxy source)
    {
        _ = source ?? throw new ArgumentNullException(nameof(source));
        var sourceMetadata = source.image.Metadata;
        var targetMetadata = this.image.Metadata;

        targetMetadata.HorizontalResolution = sourceMetadata.HorizontalResolution;
        targetMetadata.VerticalResolution = sourceMetadata.VerticalResolution;
        targetMetadata.ResolutionUnits = sourceMetadata.ResolutionUnits;
        targetMetadata.ExifProfile = sourceMetadata.ExifProfile?.DeepClone();
        targetMetadata.XmpProfile = sourceMetadata.XmpProfile?.DeepClone();
        targetMetadata.IccProfile = sourceMetadata.IccProfile?.DeepClone();
        targetMetadata.IptcProfile = sourceMetadata.IptcProfile?.DeepClone();
    }

    /// <summary>
    ///     Saves as JPEG.
    /// </summary>
    /// <param name="fileName">Name of the file.</param>
    public void SaveAsJpeg(string fileName)
    {
        this.image.SaveAsJpeg(fileName);
    }

    /// <summary>
    ///     Saves as JPEG.
    /// </summary>
    /// <param name="stream">The stream.</param>
    public void SaveAsJpeg(Stream stream)
    {
        this.image.SaveAsJpeg(stream);
    }
}
