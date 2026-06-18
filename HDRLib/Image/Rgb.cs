// Copyright (c) Stanislav Popov. All rights reserved.

using System.Runtime.InteropServices;

namespace HDRLib.Image;

[StructLayout(LayoutKind.Sequential)]
public struct Rgb
{
    const float Rw = 0.2126f, Gw = 0.7152f, Bw = 0.0722f;

    public float Red { get; set; }

    public float Green { get; set; }

    public float Blue { get; set; }

    public Rgb(float red, float green, float blue)
    {
        Red = red;
        Green = green;
        Blue = blue;
    }

    public static Rgb operator -(Rgb a, Rgb b)
    {
        return new Rgb(a.Red - b.Red, a.Green - b.Green, a.Blue - b.Blue);
    }

    public static Rgb operator /(Rgb a, Rgb b)
    {
        return new Rgb(a.Red / b.Red, a.Green / b.Green, a.Blue / b.Blue);
    }

    public static Rgb operator /(float a, Rgb b)
    {
        return new Rgb(a / b.Red, a / b.Green, a / b.Blue);
    }

    public static Rgb operator /(Rgb a, float b)
    {
        return new Rgb(a.Red / b, a.Green / b, a.Blue / b);
    }

    public static Rgb operator *(Rgb a, Rgb b)
    {
        return new Rgb(a.Red * b.Red, a.Green * b.Green, a.Blue * b.Blue);
    }

    public static Rgb operator *(Rgb a, float b)
    {
        return new Rgb(a.Red * b, a.Green * b, a.Blue * b);
    }

    public float Light() => (Rw * Red + Gw * Green + Bw * Blue);

    public void Update(float red, float green, float blue)
    {
        this.Red = red;
        this.Green = green;
        this.Blue= blue;
    }

    public override string ToString()
    {
        return $"{this.Red},{this.Green},{this.Blue}";
    }
}
