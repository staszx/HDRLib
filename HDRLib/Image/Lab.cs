// Copyright (c) Stanislav Popov. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace HDRLib.Image
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Lab
    {
        public float L { get; set; }

        public float A { get; set; }

        public float B { get; set; }

        public Lab(float L, float A, float B)
        {
            this.L = L;
            this.A = A;
            this.B = B;
        }

    }
}
