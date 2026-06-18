// Copyright (c) Stanislav Popov. All rights reserved.


namespace HDRLib.MathUtils
{
    using System.Runtime.CompilerServices;
    using System.Runtime.Intrinsics;
    using System.Runtime.Intrinsics.X86;

    internal class MathHelper
    {
        private static readonly Vector256<float> MinVectord = Vector256.Create(float.MinValue);
        private static readonly Vector256<float> MaxVectord = Vector256.Create(float.MaxValue);

        #region Methods

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static unsafe Vector256<float> MinSimd(Vector256<float>[] array)
        {
            var minV = MaxVectord;
            var len = array.Length;
            fixed (Vector256<float>* arrayP = array)
            {
                for (var i = 0; i < len; i++)
                {
                    minV = Avx.Min(minV, arrayP[i]);
                }
            }
            




            var high = Avx.Permute2x128(minV, minV, 0b_0000_0001);
            var min2 = Avx.Min(minV, high);
            var shuf = Avx.Permute(min2, 0b_1110_0001);
            var minAll = Avx.Min(min2, shuf);

            var minValue = minAll.GetElement(0);
            return Vector256.Create(minValue);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static unsafe Vector256<float> MaxSimd(Vector256<float>[] array)
        {
            var maxV = MinVectord;
            var len = array.Length;
            fixed (Vector256<float>* arrayP = array)
            {
                for (var i = 0; i < len; i++)
                {
                    maxV = Avx.Max(maxV, arrayP[i]);
                }
            }

            var high = Avx.Permute2x128(maxV, maxV, 0b_0000_0001); 
            var max2 = Avx.Max(maxV, high);
            var shuf = Avx.Permute(max2, 0b_1110_0001);          
            var maxAll = Avx.Max(max2, shuf);
            var maxValue = maxAll.GetElement(0);
            return Vector256.Create(maxValue);

            return maxV;
        }

        /// <summary>
        ///     Sums the specified array.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="array">The array.</param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static unsafe Vector256<T> Sum<T>(Vector256<T>[] array) where T : struct
        {
            var result = new Vector256<T>();
            var length = array.Length;
            fixed (Vector256<T>* pArray = array)
            {
                for (var i = 0; i < length; i++)
                {
                    result += pArray[i];
                }
            }

            return result;
        }


        /// <summary>
        ///     Initializes the array.
        /// </summary>
        /// <param name="rowCount">The row count.</param>
        /// <param name="colCount">The col count.</param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static T[][] Initialize2DArray<T>(int rowCount, int colCount)
        {
            var result = new T[rowCount][];
            Parallel.For(0, rowCount, i =>
            {
                result[i] = GC.AllocateArray<T>(colCount);
            });

            return result;
        }


        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static unsafe float Max(float[][] array, int lenX, int lenY)
        {
            var result = float.MinValue;
            for (var y = 0; y < lenY; y++)
            {
                fixed (float* arrayP = array[y])
                {
                    for (var x = 0; x < lenX; x++)
                    {
                        if (arrayP[x] > result)
                        {
                            result = arrayP[x];
                        }
                    }
                }
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static double[][] Copy(double[][] source)
        {
            var length = source.Length;
            var result = new double[length][];
            Parallel.For(0, length, i =>
            {
                var row = source[i];
                var copy = new double[row.Length];
                Array.Copy(row, copy, row.Length);
                result[i] = copy;
            });

            return result;
        }

        #endregion
    }
}