// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Hdr.Debevec
{
    using System.Drawing;
    using System.Runtime.CompilerServices;
    using Gpu;
    using Interfaces;
    using MathUtils;
    using ToneMapping;
    using ToneMapping.Settings;

    public unsafe class HDRProcessor<T> where T : IImageProxy
    {
        #region Fields

        private IRadianceMap radianceMap;
        private GpuContext? context;
        private readonly ToneMapperSettings? defaultToneMapperSettings;

        internal static float[] LutW = GetLutWeight();

        #endregion

        #region Constructors

        public HDRProcessor(GpuContext context) : this(null, context)
        {
        }

        public HDRProcessor(ToneMapperSettings? toneMapperSettings = null, GpuContext? context = null)
        {
            this.defaultToneMapperSettings = toneMapperSettings;
            this.context = context;
            this.radianceMap = this.CreateRadianceMap(this.defaultToneMapperSettings);

        }

        #endregion

        #region Methods

        [MethodImpl(MethodImplOptions.AggressiveOptimization & MethodImplOptions.AggressiveInlining)]
        internal static float Weight(float z)
        {
            return (float)Math.Exp(-Math.Pow((z - 128) /96, 2));
        }


        private static float[] GetLutWeight()
        {
            var lut = new float[256];
            Parallel.For(0, 256, i =>
            {
                lut[i] = Weight(i);
            });

            return lut;
        }
        


        public IImageProxy Process(List<IImageProxy> images, HdrImageOptions options)
        {

            return this.Build(images, options);
        }

        public IImageProxy Build(List<IImageProxy> images, HdrImageOptions options)
        {
            if (!string.IsNullOrWhiteSpace(options.SaturationFilterPresetsDirectory) &&
                options.ToneMapperSettings is not null)
            {
                options.ToneMapperSettings.SaturationFilterPresetsDirectory = options.SaturationFilterPresetsDirectory;
            }

            this.radianceMap = this.CreateRadianceMap(options.ToneMapperSettings ?? this.defaultToneMapperSettings);

         //   const float alphaMotionK = 12f / 100;
            var imageCount = images.Count;
            var width = images[0].Width;
            var height = images[0].Height;
            var sampleCount = options.SampleCount;
            var smoothFactor = options.SmoothFactor;
            var standardNumber = 0;
            var standard = images[standardNumber];

           // var motionFilterStrength = 3f;//Math.Clamp(options.MotionFilterStrength, 1f, 100f);
           var alphaMotion = 6f;//motionFilterStrength * alphaMotionK;
            var pixelsInfo = new PixelInfo[imageCount];
            Parallel.For(0, imageCount, i =>
            {
                pixelsInfo[i] = PixelInfo.Create(images[i]);
            });

            var motionMask =  MotionMask.BuildMotionMask(pixelsInfo, standardNumber, alphaMotion, 3);

            var position = new List<Point>();
            position.AddRange(this.GetStratifiedSamplePoints(standard, motionMask, (int)(sampleCount), 0.6f));
          //  position.AddRange(this.GetStratifiedSamplePoints(standard, null, sampleCount, 0));


            Parallel.For(0, imageCount, i =>
            {
                pixelsInfo[i].LoadSamples(position);
            });


            var response = new double[Const.ChannelCount][];
            Parallel.For(0, Const.ChannelCount, i => { response[i] = GSolve(pixelsInfo, smoothFactor, i, LutW); });


           // var motionMask1 = motionFilterStrength == 1 ? null : motionMask;
            radianceMap.Fill(pixelsInfo, response, motionMask, width, height);
            //radianceMap.Normalize();
          //  var pixels = this.radianceMap.GetPixels();
          //  var lab = colorConverter.FromRgb(pixels, height, width);

          

         this.radianceMap.Normalize(options);
            return radianceMap.ToImage<T>();


           
            //   this.radianceMap.Fill(pixelsInfo, response, width, height);
            //   this.radianceMap.Normalize();

        }

        private IRadianceMap CreateRadianceMap(ToneMapperSettings? toneMapperSettings)
        {
            if (this.context != null)
            {
                return new RadianceMapGpu(this.context, toneMapperSettings);
            }

            return SystemHelper.UseAvx ? new RadianceMapSIMD(toneMapperSettings) : new RadianceMap(toneMapperSettings);
        }

        private static double[] GSolve(PixelInfo[] pixelInfo, int smoothFactor, int channel, float[] lutWeight)
        {
            const int responseValueCount = Const.zMax + 1;
            var exposureCount = pixelInfo.Length;
            var sampleCount = pixelInfo[0].Rgb[0].Length;

            var normal = MathHelper.Initialize2DArray<double>(responseValueCount, responseValueCount);
            var rhs = new double[responseValueCount];
            var sampleWeightsByZ = new double[responseValueCount];
            var sampleLogByZ = new double[responseValueCount];
            var activeZ = new int[exposureCount];

            for (var sample = 0; sample < sampleCount; sample++)
            {
                var activeCount = 0;
                var sumWeight = 0d;
                var sumWeightedLogTime = 0d;

                for (var exposure = 0; exposure < exposureCount; exposure++)
                {
                    var z = pixelInfo[exposure].Rgb[channel][sample];
                    var weight = lutWeight[z];
                    var weightSquared = weight * weight;
                    if (weightSquared == 0)
                    {
                        continue;
                    }

                    if (sampleWeightsByZ[z] == 0)
                    {
                        activeZ[activeCount++] = z;
                    }

                    var weightedLogTime = weightSquared * pixelInfo[exposure].AvgLuminance;
                    sampleWeightsByZ[z] += weightSquared;
                    sampleLogByZ[z] += weightedLogTime;
                    sumWeight += weightSquared;
                    sumWeightedLogTime += weightedLogTime;
                }

                if (sumWeight > 1e-12)
                {
                    var inverseSumWeight = 1d / sumWeight;

                    for (var i = 0; i < activeCount; i++)
                    {
                        var z = activeZ[i];
                        var wz = sampleWeightsByZ[z];
                        var row = normal[z];

                        row[z] += wz;
                        rhs[z] += sampleLogByZ[z] - wz * sumWeightedLogTime * inverseSumWeight;

                        for (var j = 0; j < activeCount; j++)
                        {
                            var zz = activeZ[j];
                            row[zz] -= wz * sampleWeightsByZ[zz] * inverseSumWeight;
                        }
                    }
                }

                for (var i = 0; i < activeCount; i++)
                {
                    var z = activeZ[i];
                    sampleWeightsByZ[z] = 0;
                    sampleLogByZ[z] = 0;
                }
            }

            normal[128][128] += 1;

            for (var i = 0; i < responseValueCount - 2; ++i)
            {
                var weight = smoothFactor * (double)lutWeight[i + 1];
                AddSmoothingRow(normal, i, weight, -2 * weight, weight);
            }

            const double lambda = 1e-8;
            for (var i = 0; i < responseValueCount; i++)
            {
                normal[i][i] += lambda;
            }

            return LeastSquares.SolveLinearSystem(normal, rhs);
        }

        private static void AddSmoothingRow(double[][] normal, int startIndex, double left, double middle, double right)
        {
            AddSymmetric(normal, startIndex, startIndex, left * left);
            AddSymmetric(normal, startIndex, startIndex + 1, left * middle);
            AddSymmetric(normal, startIndex, startIndex + 2, left * right);
            AddSymmetric(normal, startIndex + 1, startIndex + 1, middle * middle);
            AddSymmetric(normal, startIndex + 1, startIndex + 2, middle * right);
            AddSymmetric(normal, startIndex + 2, startIndex + 2, right * right);
        }

        private static void AddSymmetric(double[][] matrix, int row, int column, double value)
        {
            matrix[row][column] += value;
            if (row != column)
            {
                matrix[column][row] += value;
            }
        }




        private List<Point> GetSamples(IImageProxy img, int totalSamples)
        {
            var width = img.Width;
            var height = img.Height;
            var step = Math.Sqrt(totalSamples);
            var stepY = (int)(height / step);
            var stepX = (int)(height / step);

            var result = new List<Point>();
            for (int y = stepY; y < height; y+= stepY)
            {
                for (int x = stepX; x < width; x+=stepX)
                {
                    result.Add(new Point(x,y)); 
                }
            }

            return result;
        }


        private List<Point> GetStratifiedSamplePoints(
       IImageProxy img,
       float[,] motionMask,
       int totalSamples = 3000,
       float threshold = 0.85f,
       int bins = 64,
       int seed = 42)
        {
            var rnd = new Random(seed);
            int width = img.Width;
            int height = img.Height;

            // Stratified bins by brightness
            var binsList = new List<Point>[bins];
            for (int i = 0; i < bins; i++)
                binsList[i] = new List<Point>();

            // --- Fill bins ---
            for (int y = 0; y < height; y++)
            {
                var row = img.LoadRow(y);

                for (int x = 0, p = 0; x < width; x++, p += 3)
                {
                    // ---- motion mask check ----
                    if (motionMask != null && motionMask[y, x] <= threshold)
                        continue;

                    // ---- luminance 0..255 ----
                    float l =
                        0.2126f * row[p] +
                        0.7152f * row[p + 1] +
                        0.0722f * row[p + 2];

                    // ---- bin index 0..bins-1 ----
                    int bi = (int)(l / 255f * (bins - 1));

                    binsList[bi].Add(new Point(x, y));
                }
            }

            // --- Prepare output ---
            var result = new List<Point>(totalSamples);

            // Equal distribution over bins
            int basePerBin = totalSamples / bins;
            int remainder = totalSamples % bins;

            var used = new HashSet<(int x, int y)>();

            for (int bi = 0; bi < bins; bi++)
            {
                var bucket = binsList[bi];
                int take = basePerBin + (bi < remainder ? 1 : 0);

                if (bucket.Count == 0)
                    continue;

                // Shuffle bucket
                for (int i = bucket.Count - 1; i > 0; i--)
                {
                    int j = rnd.Next(i + 1);
                    (bucket[i], bucket[j]) = (bucket[j], bucket[i]);
                }

                take = Math.Min(take, bucket.Count);

                for (int k = 0; k < take; k++)
                {
                    var p = bucket[k];
                    if (used.Add((p.X, p.Y)))
                        result.Add(p);
                }
            }

            // If still not enough (rare), fill from non-empty bins
            if (result.Count < totalSamples)
            {
                for (int tries = 0; tries < totalSamples * 2 && result.Count < totalSamples; tries++)
                {
                    int bi = rnd.Next(bins);
                    var bucket = binsList[bi];
                    if (bucket.Count == 0)
                        continue;

                    var p = bucket[rnd.Next(bucket.Count)];
                    if (used.Add((p.X, p.Y)))
                        result.Add(p);
                }
            }

            return result;
        }



        #endregion
    }
}
