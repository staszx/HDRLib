// Copyright (c) Stanislav Popov. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhotoProcessor
{
    using HDRLib.Interfaces;
    using HDRLib.PixelProvider.ImageSharp;

    internal static class Helper
    {
        public static List<IImageProxy> Load (List<string> input)
        {
            var result = new List<IImageProxy>();
            foreach (var fileName in input)
            {
                var image = new ImageSharpProxy();
                image.Load(fileName);
                result.Add(image);
            }

            return result;
        }
    }
}
