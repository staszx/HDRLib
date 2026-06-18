// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Hdr.Debevec;
using Interfaces;

using System.Runtime.CompilerServices;
using ToneMapping;
using ToneMapping.Settings;
using HDRLib.Image;
using HDRLib.ToneMapping.Factories;

internal class RadianceMap : IRadianceMap
{
    #region Fields

    public Image<Rgb> Image;

    private int height;

    private int width;
    private float targetAverageBrightness = 1f;
    private readonly ToneMapperSettings? toneMapperSettings;


    #endregion


    #region Methods

    public RadianceMap(ToneMapperSettings? toneMapperSettings = null)
    {
        this.toneMapperSettings = toneMapperSettings;
    }


    public void Prepare(int width, int height)
    {
        this.width = width;
        this.height = height;
        this.Image = new Image<Rgb>(width, height);

    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Calc(double[] g, double[] t, double[] w, double sumw)
    {
        var size = g.Length;
        var result = 0d;
        for (var i = 0; i < size; i++)
        {
            result += (g[i] - t[i]) * w[i] / sumw;
        }

        return result;
    }

    private byte Clamp(double value)
    {
        value = Math.Round(value);
        if (value > 255)
        {
            value = 255;
        }

        if (value < 0)
        {
            value = 0;
        }

        return (byte)value;
    }

    #endregion

    public unsafe void Fill(PixelInfo[] pixelInfo, double[][] response, float[,] motionMask, int width, int height)
    {
        this.Prepare(width, height);
        this.targetAverageBrightness = HdrBrightnessNormalizer.CalculateTargetAverageBrightness(pixelInfo, width, height);
        var imageSize = pixelInfo.Length;
        var logTimes = pixelInfo.Select(i => i.AvgLuminance).ToArray();
        var fallbackImageIndex = Array.IndexOf(logTimes, logTimes.Min());
        using var handle = new PinnedArray<Rgb>(this.Image.Pixels);
        var pixels = handle.Pointer;
        var idx = 0;
        var values = stackalloc double[Const.ChannelCount];
        for (var y = 0; y < this.height; ++y)
        {
            var rows = new List<byte[]>();
            for (var i = 0; i < imageSize; i++)
            {
                rows.Add(pixelInfo[i].LoadRow(y));
            }

            for (var x = 0; x < this.width; x++, idx++)
            {
                var g = GC.AllocateUninitializedArray<double>(imageSize);
                var w = GC.AllocateUninitializedArray<double>(imageSize);
                var k = x * Const.ChannelCount;
                var motionWeightValue = motionMask == null ? 1f : motionMask[y, x] > 0.6f ? 1f : 0f;
                for (var c = 0; c < Const.ChannelCount; c++)
                {
                    var sumW = 0d;
                    for (var i = 0; i < imageSize; i++)
                    {
                        var motionWeight = i == 0 ? 1f : motionWeightValue;
                        var value = rows[i][k + c];
                        g[i] = response[c][value];
                        var red = rows[i][k];
                        var green = rows[i][k + 1];
                        var blue = rows[i][k + 2];
                        var colorWeight = Math.Min(
                            HDRProcessor<IImageProxy>.LutW[red],
                            Math.Min(
                                HDRProcessor<IImageProxy>.LutW[green],
                                HDRProcessor<IImageProxy>.LutW[blue]));
                        w[i] = colorWeight * motionWeight;
                        sumW += w[i];
                    }

                    
                    var fallbackPixel = rows[fallbackImageIndex][k + c];
                    values[c] = sumW > 0 ? Calc(g, logTimes, w, sumW) : response[c][fallbackPixel] - logTimes[fallbackImageIndex];
                }

                pixels[idx].Update((float)Math.Exp(values[0]), (float)Math.Exp(values[1]), (float)Math.Exp(values[2]));
            }
        }


    }
    private static unsafe Rgb Max(Rgb[] pixels, long length)
    {
        var max = new Rgb(float.MinValue, float.MinValue, float.MinValue);

        for (var i = 0; i < length; i++)
        {
            var p = pixels[i];
            if (p.Red > max.Red)
            {
                max.Red = p.Red;
            }

            if (p.Green > max.Green)
            {
                max.Green = p.Green;
            }

            if (p.Blue > max.Blue)
            {
                max.Blue = p.Blue;
            }
        }

        return max;
    }

    public unsafe void Normalize(HDRLib.HdrImageOptions options)
    {

        var averageBrightness = HdrBrightnessNormalizer.CalculateAverageBrightness(this.Image.Pixels, this.Image.Pixels.Length);
        var k = this.targetAverageBrightness / MathF.Max(averageBrightness, 1e-6f);
        for (var i = 0; i < this.Image.Pixels.Length; i++)
        {
            this.Image.Pixels[i] *= k;
        }

        if (this.toneMapperSettings is not null)
        {
            var toneMapper = ToneMapperFactory.Create(this.toneMapperSettings);
            toneMapper.ApplyInPlace(this.Image);
        }

        for (int i = 0; i < this.Image.Pixels.Length; i++)
        {
            this.Image.Pixels[i] *= 255;
        }

    }

    public unsafe IImageProxy ToImage<T>() where T : IImageProxy
    {

        var image = (IImageProxy)Activator.CreateInstance(typeof(T));
        image.Create(this.width, this.height);
        using var handle = new PinnedArray<Rgb>(this.Image.Pixels);
        var pixels = handle.Pointer;
        Parallel.For(0, this.height, y =>

        {
            var row = GC.AllocateUninitializedArray<byte>(this.width * 3);
            var idx = 0;

            fixed (byte* rowP = row)
            {
                var inputIdx = y * this.width;
                for (var x = 0; x < this.width; x++)
                {
                    var pixel = pixels[inputIdx + x];
                    rowP[idx++] = this.Clamp(pixel.Red);
                    rowP[idx++] = this.Clamp(pixel.Green);
                    rowP[idx++] = this.Clamp(pixel.Blue);
                }

                image.SaveRow(y, row);
            }
        });
        return image;
    }


    public Rgb[] GetPixels()
    {
        return this.Image.Pixels;
   
    }

    public void SetPixels(double[][][] pixels)
    {
        // this.Pixels = pixels;
        throw new Exception();
    }
}
