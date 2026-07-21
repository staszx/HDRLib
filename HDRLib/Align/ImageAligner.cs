// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Align;

using Gpu;
using Interfaces;

/// <summary>
///     The Image Aligner
/// </summary>
public abstract class ImageAligner
{
    #region Fields

    /// <summary>
/// Collection of images to be aligned. May be null until initialized.
/// </summary>
protected IList<IImageProxy>? images;

    /// <summary>
    /// Gets or sets a value indicating whether areas exposed by alignment are filled from the reference image.
    /// </summary>
    public bool FillOutsideWithReference { get; set; }

    #endregion

    #region Constructors


    public static ImageAligner Create()
    {
        return SystemHelper.UseAvx ? new ImageAlignerPyramidSimd() : new ImageAlignerPyramidClassic();
    }

    public static ImageAligner Create(GpuContext context)
    {
        return new ImageAlignerPyramidGpu(context);
    }

    /// <summary>
/// Initializes a new instance without pre‑loaded images.
/// </summary>
protected ImageAligner()
    {
    }

    /// <summary>
/// Initializes a new instance with a predefined image collection.
/// </summary>
/// <param name="images">Images to align.</param>
protected ImageAligner(IList<IImageProxy> images)
    {
        this.images = images;
    }

    #endregion

    #region Methods

    public virtual void Process(IList<IImageProxy> images)
    {
        this.images = images;
        if (this.images.Count == 0)
        {
            throw new InvalidOperationException("ImageAligner requires non-empty images.");
        }
    }

    #endregion
}
