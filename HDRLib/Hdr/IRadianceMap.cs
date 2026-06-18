// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Hdr.Debevec
{
    using HDRLib.Image;
    using Interfaces;
    using ToneMapping.Settings;

    internal interface IRadianceMap
    {
        void Fill(PixelInfo[] pixelInfo, double[][] response, float[,] motionMask, int width, int height);
        void Normalize(HDRLib.HdrImageOptions options);
        IImageProxy ToImage<T>() where T : IImageProxy;

        Rgb[] GetPixels();

        void SetPixels(double[][][] pixels);
    }
}
