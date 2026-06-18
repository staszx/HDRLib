// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.PixelProvider.ImageSharp;

using Interfaces;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;

/// <summary>
/// </summary>
/// <seealso cref="HDRLib.Interfaces.IImageProcessor" />
public class ImageSharpProcessor :  IImageProcessor
{
    #region Fields

    /// <summary>
    ///     The image
    /// </summary>
    private readonly Image<Rgb24> image;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageSharpProcessor" /> class.
    /// </summary>
    /// <param name="image">The image.</param>
    public ImageSharpProcessor(Image<Rgb24> image)
    {
        this.image = image;
    }

    #endregion

    /// <summary>
    ///     Contrasts the specified value.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns></returns>
    public void Contrast(int value)
    {
        var v = value / 100f;
        this.image.Mutate(context => context.Contrast(v));
    }

    /// <summary>
    ///     Brightnesses the specified value.
    /// </summary>
    /// <param name="value">The value.</param>
    public void Brightness(int value)
    {
        var v = value / 100f;
        this.image.Mutate(context => context.Brightness(v));
    }

    /// <summary>
    ///     Saturations the specified value.
    /// </summary>
    /// <param name="value">The value.</param>
    public void Saturation(int value)
    {
        var v = value / 100f;
        this.image.Mutate(context => context.Saturate(v));
    }

    /// <summary>
    ///     Colors the temperature.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <exception cref="System.NotImplementedException"></exception>
    public void ColorTemperature(int value)
    {
        throw new NotImplementedException();
    }


    /// <summary>
    /// Resizes the specified width.
    /// </summary>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public void Resize(int width, int height)
    {
        this.image.Mutate(i => i.Resize(width, height));
    }

    public void DrawImage(IImageProxy sourceImage, int x, int y)
    {
        throw new NotImplementedException();
    }

    public void BinaryMap(float value)
    {
        this.image.Mutate(i =>
        {
            i.Grayscale(GrayscaleMode.Bt709);
            i.BinaryThreshold(0.2f);
            i.GaussianBlur(value*10);
           // i.DetectEdges(KnownEdgeDetectorKernels.Kayyali);
        });
    }

    public void Grayscale()
    {
       this.image.Mutate(i =>
       {
           i.Grayscale(GrayscaleMode.Bt709);




        //   i.BinaryThreshold(0.2f);
        //   i.GaussianBlur(2f);
          // i.DetectEdges(KnownEdgeDetectorKernels.Sobel);
       });

       
    }

    public void GaussianBlur(float sigma)
    {
        this.image.Mutate(i=>i.GaussianBlur(sigma));
    }
}