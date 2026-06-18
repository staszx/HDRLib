// Copyright (c) Stanislav Popov. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HDRLib.Adjust
{
    public struct VibranceStats
    {
        public float Lsum;
        public float Lsum2;
        public float Csum;
        public float Cmax;
        public int Count;
    }
}
