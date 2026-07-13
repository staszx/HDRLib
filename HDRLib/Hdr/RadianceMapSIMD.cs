// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Hdr
{
    using System.Runtime.CompilerServices;
    using System.Runtime.Intrinsics;
    using System.Runtime.Intrinsics.X86;
    using Debevec;
    using HDRLib.Image;
    using HDRLib.ToneMapping.Factories;
    using Interfaces;
    using MathUtils;
    using Post;
    using PostProcessors;
    using ToneMapping;
    using ToneMapping.Settings;

    internal class RadianceMapSIMD : IRadianceMap
    {
        #region Fields
        private static readonly Vector256<float> vectorzAvg = Vector256.Create((float)Const.zAvg);
        private static readonly Vector256<float> vectorzMin = Vector256.Create((float)Const.zMin);
        private static readonly Vector256<float> vectorzMax = Vector256.Create((float)Const.zMax);
        public Vector256<float>[][] Pixels;
        public int vectorSize = Vector256<float>.Count;
        public Vector256<float> vectorZero = Vector256<float>.Zero;
        private int height;
        private int vectorLength;
        private int vectorWidth;
        private int width;
        private int length;
        private float targetAverageBrightness = 1f;
        private readonly ToneMapperSettings? toneMapperSettings;

        #endregion

        public RadianceMapSIMD(ToneMapperSettings? toneMapperSettings = null)
        {
            this.toneMapperSettings = toneMapperSettings;
        }

        #region Methods

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Exp()
        {
            var pixels = Pixels;
            Parallel.For(0, Const.ChannelCount, ch =>
            {
                fixed (Vector256<float>* pxls = pixels[ch])
                {
                    for (var i = 0; i < this.vectorLength; ++i)
                    {
                        pxls[i] = AvxMath.Exp(pxls[i]);
                    }
                }
            });
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Log()
        {
            var pixels = Pixels;
            Parallel.For(0, Const.ChannelCount, ch =>
            {
                fixed (Vector256<float>* pxls = pixels[ch])
                {
                    for (var i = 0; i < this.vectorLength; ++i)
                    {
                        pxls[i] = AvxMath.Ln(pxls[i]);
                    }
                }
            });
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<float> Calc(Vector256<float>[] g, Vector256<float>[] t, Vector256<float>[] w, Vector256<float> sumw)
        {
            var size = g.Length;
            var result = new Vector256<float>();
            for (var i = 0; i < size; i++)
            {
                result += (g[i] - t[i]) * w[i] / sumw;
            }

            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte Clamp(float value)
        {
            value = MathF.Round(value);
            if (value > 255)
            {
                value = 255;
            }

            if (value < 0)
            {
                value = 0;
            }

            return (byte)value;
        }

        #endregion

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<float> Weight(Vector256<float> z)
        {
            var lowWeight = (z - vectorzMin) / vectorzAvg;
            var highWeight = (vectorzMax - z) / vectorzAvg;
            var useLow = Avx.CompareLessThanOrEqual(z, vectorzAvg);
            var weight = Avx.BlendVariable(highWeight, lowWeight, useLow);
            return Avx.Max(Vector256<float>.Zero, weight);
        }

        private void Prepare(int width, int height)
        {
            this.length = width * height;
            this.vectorLength = this.length/this.vectorSize;
            this.vectorWidth = width / this.vectorSize;
            this.width = width;
            this.height = height;

            Pixels = new Vector256<float>[Const.ChannelCount][];
            Parallel.For(0, Const.ChannelCount, c =>
            {
                Pixels[c] = GC.AllocateUninitializedArray<Vector256<float>>(this.vectorWidth * height);
                
            });
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public unsafe void Fill(PixelInfo[] pixelInfo, double[][] response, float[,] motionMask, int width, int height)
        {
            this.targetAverageBrightness = HdrBrightnessNormalizer.CalculateTargetAverageBrightness(pixelInfo, width, height);
            Prepare(width, height);
            var vectorLength = Vector256<float>.Count;
            var vectorOne = Vector256.Create(1f);
            var vectorMotionThreshold = Vector256.Create(0.6f);
            var imageSize = pixelInfo.Length;
            var scalarLogTimes = pixelInfo.Select(i => (float)i.AvgLuminance).ToArray();
            var fallbackImageIndex = Array.IndexOf(scalarLogTimes, scalarLogTimes.Min());
            var logTimes = scalarLogTimes.Select(Vector256.Create).ToArray();
            var lutW = HDRProcessor<IImageProxy>.LutW;
            var pixels = Pixels;
            Parallel.For(0, Const.ChannelCount, c =>
            {
                fixed (double* responseP = response[c])
                {
                    fixed (Vector256<float>* pPixels = pixels[c])
                    {
                        var pxls = pPixels;
                        for (var y = 0; y < height; ++y)
                        {
                            var rows = new List<float[][]>();
                            for (var i = 0; i < imageSize; i++)
                            {
                                rows.Add(pixelInfo[i].LoadRowByCahnels(y));
                            }

                           
                            for (var x = 0; x < width; x += vectorLength)
                            {
                                var g = new Vector256<float>[imageSize];
                                var w = new Vector256<float>[imageSize];
                                var sumW = this.vectorZero;
                                var motionMaskValue = motionMask == null
                                    ? vectorOne
                                    : Avx.BlendVariable(
                                        Vector256<float>.Zero,
                                        vectorOne,
                                        Avx.CompareGreaterThan(
                                            Vector256.Create(
                                                motionMask[y, x],
                                                motionMask[y, x + 1],
                                                motionMask[y, x + 2],
                                                motionMask[y, x + 3],
                                                motionMask[y, x + 4],
                                                motionMask[y, x + 5],
                                                motionMask[y, x + 6],
                                                motionMask[y, x + 7]),
                                            vectorMotionThreshold));

                                for (var i = 0; i < imageSize; i++)
                                {
                                   var mmv = i == 0 ? vectorOne : motionMaskValue;
                                    fixed (float* pRow = rows[i][c])
                                    fixed (float* pRow0 = rows[i][0])
                                    fixed (float* pRow1 = rows[i][1])
                                    fixed (float* pRow2 = rows[i][2])
                                    {
                                        var pr = pRow + x;
                                        var value = Vector256.Load(pr);
                                        var red = Vector256.Load(pRow0 + x);
                                        var green = Vector256.Load(pRow1 + x);
                                        var blue = Vector256.Load(pRow2 + x);
                                        var colorWeight = Avx.Min(
                                            Vector256.Create(
                                                lutW[(int)red[0]],
                                                lutW[(int)red[1]],
                                                lutW[(int)red[2]],
                                                lutW[(int)red[3]],
                                                lutW[(int)red[4]],
                                                lutW[(int)red[5]],
                                                lutW[(int)red[6]],
                                                lutW[(int)red[7]]),
                                            Avx.Min(
                                                Vector256.Create(
                                                    lutW[(int)green[0]],
                                                    lutW[(int)green[1]],
                                                    lutW[(int)green[2]],
                                                    lutW[(int)green[3]],
                                                    lutW[(int)green[4]],
                                                    lutW[(int)green[5]],
                                                    lutW[(int)green[6]],
                                                    lutW[(int)green[7]]),
                                                Vector256.Create(
                                                    lutW[(int)blue[0]],
                                                    lutW[(int)blue[1]],
                                                    lutW[(int)blue[2]],
                                                    lutW[(int)blue[3]],
                                                    lutW[(int)blue[4]],
                                                    lutW[(int)blue[5]],
                                                    lutW[(int)blue[6]],
                                                    lutW[(int)blue[7]])));
                                        g[i] = Vector256.Create(
                                            (float)responseP[(int)value[0]],
                                            (float)responseP[(int)value[1]],
                                            (float)responseP[(int)value[2]],
                                            (float)responseP[(int)value[3]],
                                            (float)responseP[(int)value[4]],
                                            (float)responseP[(int)value[5]],
                                            (float)responseP[(int)value[6]],
                                            (float)responseP[(int)value[7]]);
                                        w[i] = Avx.Multiply(colorWeight, mmv); 
                                        sumW += w[i];
                                    }
                                }

                                //var sumW = MathHelper.Sum(w);
                                var mask = Avx.CompareGreaterThan(sumW, Vector256<float>.Zero);
                                var calcRes = Calc(g, logTimes, w, sumW);
                                Vector256<float> fallback;
                                fixed (float* pFallbackRow = rows[fallbackImageIndex][c])
                                {
                                    var fallbackValue = Vector256.Load(pFallbackRow + x);
                                    fallback = Vector256.Create(
                                        (float)responseP[(int)fallbackValue[0]],
                                        (float)responseP[(int)fallbackValue[1]],
                                        (float)responseP[(int)fallbackValue[2]],
                                        (float)responseP[(int)fallbackValue[3]],
                                        (float)responseP[(int)fallbackValue[4]],
                                        (float)responseP[(int)fallbackValue[5]],
                                        (float)responseP[(int)fallbackValue[6]],
                                        (float)responseP[(int)fallbackValue[7]]) - logTimes[fallbackImageIndex];
                                }

                                var result = Avx.BlendVariable(fallback, calcRes, mask);
                                *pxls++ = result;
                            }
                        }
                    }
                }

            });

            Exp();

        }
        
        public unsafe void Normalize(HDRLib.HdrImageOptions options)
        {
            if (this.toneMapperSettings is null)
            {
                var averageBrightness = this.CalculateAverageBrightness();
                var scale = this.targetAverageBrightness / MathF.Max(averageBrightness, 1e-6f) * 255f;
                this.Multiply(scale);
                return;
            }

            if (this.toneMapperSettings is not null)
            {
                var toneMapper = ToneMapperFactorySIMD.Create(this.toneMapperSettings);
                toneMapper.ApplyHdrInPlace(this.Pixels, this.width, this.height);
            }


            Parallel.For(0, Const.ChannelCount, ch =>
            {
                fixed (Vector256<float>* pxls = this.Pixels[ch])
                {
                    for (var i = 0; i < this.vectorLength; ++i)
                    {
                        pxls[i] *= 255f;
                    }
                }
            });
        }

        private unsafe void Multiply(float scale)
        {
            var scaleVector = Vector256.Create(scale);
            Parallel.For(0, Const.ChannelCount, ch =>
            {
                fixed (Vector256<float>* pxls = this.Pixels[ch])
                {
                    for (var i = 0; i < this.vectorLength; ++i)
                    {
                        pxls[i] *= scaleVector;
                    }
                }
            });
        }

        private float CalculateAverageBrightness()
        {
            const float rw = 0.2126f;
            const float gw = 0.7152f;
            const float bw = 0.0722f;

            var sum = 0f;
            for (var y = 0; y < this.height; y++)
            {
                var rowOffset = y * this.vectorWidth;
                for (var x = 0; x < this.width; x++)
                {
                    var vectorIndex = rowOffset + (x / Vector256<float>.Count);
                    var elementIndex = x % Vector256<float>.Count;
                    sum +=
                        (this.Pixels[0][vectorIndex].GetElement(elementIndex) * rw) +
                        (this.Pixels[1][vectorIndex].GetElement(elementIndex) * gw) +
                        (this.Pixels[2][vectorIndex].GetElement(elementIndex) * bw);
                }
            }

            return this.width == 0 || this.height == 0 ? 0f : sum / (this.width * this.height);
        }

        public unsafe IImageProxy ToImage<T>() where T : IImageProxy
        {
            var pixels = Pixels;
            var image = (IImageProxy)Activator.CreateInstance(typeof(T));
            var vectorWidth = this.vectorWidth;
            image.Create(width, height);
            using var handle0 = new PinnedArray<Vector256<float>>(this.Pixels[0]);
            using var handle1 = new PinnedArray<Vector256<float>>(this.Pixels[1]);
            using var handle2= new PinnedArray<Vector256<float>>(this.Pixels[2]);
            var pxls0 = handle0.Pointer;
            var pxls1 = handle1.Pointer;
            var pxls2 = handle2.Pointer;
           for (int y = 0; y < height; y++)
           {
                var row = GC.AllocateUninitializedArray<byte>(width * 3);
                
                var idxDst = 0;
                fixed (byte* rowP = row)
                {
                    var inputIdx = y * vectorWidth;
                    for (var x = 0; x < vectorWidth; x++, inputIdx++)
                    {
                        for (var i = 0; i < vectorSize; i++)
                        {
                            rowP[idxDst++] = Clamp(pxls0[inputIdx][i]);
                            rowP[idxDst++] = Clamp(pxls1[inputIdx][i]);
                            rowP[idxDst++] = Clamp(pxls2[inputIdx][i]);
                        }
                    }

                }

                image.SaveRow(y, row);
            };


            return image;
        }

        public Rgb[] GetPixels()
        {
            return null;
        }

        public void SetPixels(double[][][] pixels)
        {

        }
    }

}
