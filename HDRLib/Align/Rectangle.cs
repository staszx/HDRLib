// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Align;

public struct Rectangle
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public int HalfHeight => Height / 2;

    public int HalfWidth => Width / 2;

    public int Right => Left + Width;

    public int Bottom => Top + Height;

    public int CenterX => Left + HalfWidth;

    public int CenterY => Top + HalfHeight;



    public Rectangle(int left, int top, int width, int height)
    {
        this.Left = left;
        this.Top = top;
        this.Width = width;
        this.Height = height;
    }

    public Rectangle()
    {
        this.Left = 0;
        this.Top = 0;
        this.Width = 0;
        this.Height = 0;
    }

    public static Rectangle Intersect(Rectangle a, Rectangle b)
    {
        int x = Math.Max(a.Left, b.Left);
        int num2 = Math.Min((a.Left + a.Width), (b.Left + b.Width));
        int y = Math.Max(a.Top, b.Top);
        int num4 = Math.Min((a.Top + a.Height), (b.Top + b.Height));
        if ((num2 >= x) && (num4 >= y))
        {
            return new Rectangle(x, y, num2 - x, num4 - y);
        }

        return new Rectangle();
    }

}