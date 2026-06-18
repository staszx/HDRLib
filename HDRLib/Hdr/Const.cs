// Copyright (c) Stanislav Popov. All rights reserved.


namespace HDRLib.Hdr.Debevec
{
    internal class Const
    {
        #region Constants

        public const int ChannelCount = 3;
        public const double zAvg = (zMin + zMax) * 1.00 / 2;
        public const int zMax = 255;
        public const int zMin = 0;

        #endregion
    }
}