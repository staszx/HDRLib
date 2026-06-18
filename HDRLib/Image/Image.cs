// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Image;

public class Image<T>
{
    #region Properties

    public T[] Pixels { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public long Length => Pixels.Length;

    public Image(int width, int height)
    {
        Pixels = new T[width * height];
    }

    #endregion
}