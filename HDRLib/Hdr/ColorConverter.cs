// Copyright (c) Stanislav Popov. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HDRLib.Image;

namespace HDRLib.Hdr
{
    public static class ColorConverter
    {
        // sRGB > XYZ > LAB (D65)
        public static Lab RgbToLab(Rgb p)
        {
            // 1. Нормализуем
            double r = p.Red / 255.0;
            double g = p.Green / 255.0;
            double b = p.Blue / 255.0;

            // 2. Gamma remove (linearize)
            r = r <= 0.04045 ? r / 12.92 : Math.Pow((r + 0.055) / 1.055, 2.4);
            g = g <= 0.04045 ? g / 12.92 : Math.Pow((g + 0.055) / 1.055, 2.4);
            b = b <= 0.04045 ? b / 12.92 : Math.Pow((b + 0.055) / 1.055, 2.4);

            // 3. Linear RGB > XYZ
            double X = (0.4124564 * r + 0.3575761 * g + 0.1804375 * b);
            double Y = (0.2126729 * r + 0.7151522 * g + 0.0721750 * b);
            double Z = (0.0193339 * r + 0.1191920 * g + 0.9503041 * b);

            // Reference white D65
            X /= 0.95047;
            Y /= 1.00000;
            Z /= 1.08883;

            // 4. XYZ > Lab
            Func<double, double> f = (t) =>
                t > 0.008856 ? Math.Pow(t, 1.0 / 3.0) : (7.787 * t + 16.0 / 116.0);

            double fx = f(X);
            double fy = f(Y);
            double fz = f(Z);

            double L = 116 * fy - 16;
            double A = 500 * (fx - fy);
            double B = 200 * (fy - fz);

            return new Lab((float)L, (float)A, (float)B);
        }
    }
}
