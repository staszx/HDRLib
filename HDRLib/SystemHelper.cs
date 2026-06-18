// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib
{
    using System.Runtime.Intrinsics.X86;

    public class SystemHelper
    {
        #region Properties

        internal static bool UseAvx
        {
            get
            {
                switch (UseAvxState)
                {
                    case UseAvxState.Auto: return Vx2IsSupported;
                    case UseAvxState.Enable: return true;
                    case UseAvxState.Disable:return false;
                }

                return Vx2IsSupported;
            }
        }

        public static bool Vx2IsSupported => Avx2.IsSupported;

        public static UseAvxState UseAvxState { get; set; } = UseAvxState.Auto;


        #endregion
    }

    public enum UseAvxState
    {
        Auto,
        Enable,
        Disable
    }


}