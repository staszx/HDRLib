// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Hdr.Debevec;

using System.Drawing;
using Interfaces;
using MathUtils;
using static System.Net.Mime.MediaTypeNames;

/// <summary>
/// The pixel info
/// </summary>
internal class PixelInfo
{
    // reflected-light meter calibration constant
    private const float K = 12.07488f;

    #region Properties

    /// <summary>
    /// Gets or sets the RGB.
    /// </summary>
    /// <value>
    /// The RGB.
    /// </value>
    public byte[][] Rgb { get; set; }

    /// <summary>
    /// Gets or sets the exposure time.
    /// </summary>
    /// <value>
    /// The exposure time.
    /// </value>
    public double ExposureTime { get; private set; } = -1d;

    /// <summary>
    /// Gets or sets the f number.
    /// </summary>
    /// <value>
    /// The f number.
    /// </value>
    public double FNumber { get; private set; } = -1d;

    /// <summary>
    /// Gets the iso speed value.
    /// </summary>
    /// <value>
    /// The iso speed value.
    /// </value>
    public double IsoSpeedValue { get; private set; } = 100d;

    /// <summary>
    /// Gets the ev compensation.
    /// </summary>
    /// <value>
    /// The ev compensation.
    /// </value>
    public double EvCompensation { get; private set; }

    /// <summary>
    /// Gets the average luminance.
    /// </summary>
    /// <value>
    /// The average luminance.
    /// </value>
    public double AvgLuminance { get; private set; }

    /// <summary>
    /// Gets the exposure value.
    /// </summary>
    /// <value>
    /// The exposure value.
    /// </value>
    public double ExposureValue { get; private set; }

    /// <summary>
    /// The image
    /// </summary>
    public IImageProxy Image { get; private set; }


    #endregion

    #region Methods

    /// <summary>
    /// Loads the specified image.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <returns></returns>
    public static PixelInfo Create(IImageProxy image)
    {
        var pixelInfo = new PixelInfo();
        pixelInfo.Image = image;

        if (image.ExposureTime != null)
        {
            pixelInfo.ExposureTime = image.ExposureTime.Value;
        }
        else if (image.ShutterSpeedValue!= null)
        {
            long num = 1;
            long div = 1;
            var tmp = Math.Exp(Math.Log(2.0f) * image.ShutterSpeedValue.Value);
            if (tmp > 1)
            {
                div = (long)(tmp + 0.5f);
            }
            else
            {
                num = (long)(1.0f / tmp + 0.5f);
            }

            pixelInfo.ExposureTime = (double)num/ div;
        }


        if (image.FNumber != null)
        {
            pixelInfo.FNumber = image.FNumber.Value;
        }
        else if (image.AppertureValue!=null)
        {
            pixelInfo.FNumber = (double)Math.Exp(Math.Log(2.0d) * image.AppertureValue.Value / 2d);
        }

        if (pixelInfo.FNumber == 0)
        {
            pixelInfo.FNumber = -1d;
        }

        if (image.IsoSpeedRating != null)
        {
            pixelInfo.IsoSpeedValue = image.IsoSpeedRating.Value;
        }

        if (image.ExposureBiasValue != null)
        {
            pixelInfo.EvCompensation = image.ExposureBiasValue.Value;
        }

        if (image.IsoSpeedRating != null && image.FNumber != null && image.ExposureTime != null)
        {
            pixelInfo.AvgLuminance = Math.Log(pixelInfo.ExposureTime)
                                               + Math.Log(pixelInfo.IsoSpeedValue / 100.0)
                                               - 2 * Math.Log(pixelInfo.FNumber);
            //pixelInfo.AvgLuminance = Math.Log(pixelInfo.ExposureTime * pixelInfo.IsoSpeedValue/(pixelInfo.FNumber * pixelInfo.FNumber  * 100 * K));
        }
        else
        {
            pixelInfo.AvgLuminance = -1d;
        }

        if (image.FNumber != null && image.ExposureTime != null)
        {
            pixelInfo.ExposureValue = Math.Log((pixelInfo.FNumber * pixelInfo.FNumber) / pixelInfo.ExposureTime * pixelInfo.IsoSpeedValue, 2.0f);
        }
        else
        {
            pixelInfo.ExposureTime = -100000d;
        }


        return pixelInfo;
    }

    public void LoadSamples(List<Point> samples)
    {
        var length = samples.Count;
        var rgb = MathHelper.Initialize2DArray<byte>(Const.ChannelCount, length);

        for (var i = 0; i < length; i++)
        {
            var sample = samples[i];
            var pixel = this.Image.GetPixel(sample.X, sample.Y);
            for (int j = 0; j < Const.ChannelCount; j++)
            {
                rgb[j][i] = pixel[j];
            }
        }

        this.Rgb = rgb;
    }

    public float[][] LoadRowByCahnels(int index)
    {
        var channel = new float[Const.ChannelCount][];
        var row = Image.LoadRow(index);
        var length = Image.Width;
        channel[0] = GC.AllocateUninitializedArray<float>(Image.Width);
        channel[1] = GC.AllocateUninitializedArray<float>(Image.Width);
        channel[2] = GC.AllocateUninitializedArray<float>(Image.Width);
        var idx = 0;
        unsafe
        {
            fixed (byte* pRow = row)
            fixed (float* pChannel0 = channel[0], pChannel1 = channel[1], pChannel2 = channel[2])
            {
                for (var j = 0; j < length; j++)
                {
                    pChannel0[j] = pRow[idx++];
                    pChannel1[j] = pRow[idx++];
                    pChannel2[j] = pRow[idx++];
                }
            }
        }

        return channel;
    }

    public byte[] LoadRow(int index)
    {
        return Image.LoadRow(index);
    }

    public unsafe double[] LoadRowAsDouble(int index)
    {
        var bytes = this.Image.LoadRow(index);
        var result = new double[bytes.Length];
        fixed (byte* pbytes = bytes)
        fixed (double* presult = result)
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                presult[i] = pbytes[i];
            }
        }

        return result;
    }
    public Span<byte> LoadFullImage()
    {
        var bytes = new Span<byte>(new byte[Image.Width*Image.Height*3]);
        Image.LoadFullImage(bytes);
        return bytes;
    }



    #endregion
}
