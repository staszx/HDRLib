// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Hdr.Debevec
{
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
            var distanceFromClipping = Math.Min(z, Const.zMax - z);
            var edge = Math.Clamp(distanceFromClipping / 16f, 0f, 1f);
            var edgeTaper = edge * edge * (3f - (2f * edge));
            return (float)Math.Exp(-Math.Pow((z - 128) / 96, 2)) * edgeTaper;
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

            var imageCount = images.Count;
            var width = images[0].Width;
            var height = images[0].Height;
            var sampleCount = options.SampleCount;
            var smoothFactor = options.SmoothFactor;
            var pixelsInfo = new PixelInfo[imageCount];
            Parallel.For(0, imageCount, i =>
            {
                pixelsInfo[i] = PixelInfo.Create(images[i]);
            });

            var referenceIndex = ResponseCurveSampleSelector.SelectReferenceImageIndex(pixelsInfo);
            if (referenceIndex != 0)
            {
                (pixelsInfo[0], pixelsInfo[referenceIndex]) = (pixelsInfo[referenceIndex], pixelsInfo[0]);
            }

            const int standardNumber = 0;
            var motionMask = CreateMotionMask(pixelsInfo, standardNumber, options.MotionFilterStrength);
            var position = ResponseCurveSampleSelector.Select(pixelsInfo, motionMask, sampleCount);

            Parallel.For(0, imageCount, i =>
            {
                pixelsInfo[i].LoadSamples(position);
            });


            var response = new double[Const.ChannelCount][];
            var useAvxCurveSolver = SystemHelper.UseAvx;
            Parallel.For(0, Const.ChannelCount, i =>
            {
                response[i] = GSolve(pixelsInfo, smoothFactor, i, LutW, useAvxCurveSolver);
            });
            var inliers = BuildResponseCurveInlierMask(pixelsInfo, response, LutW);
            if (inliers.Any(inlier => !inlier))
            {
                Parallel.For(0, Const.ChannelCount, i =>
                {
                    response[i] = GSolve(pixelsInfo, smoothFactor, i, LutW, useAvxCurveSolver, inliers);
                });
            }

            this.radianceMap.Fill(pixelsInfo, response, motionMask!, width, height);
            this.radianceMap.Normalize(options);
            return this.radianceMap.ToImage<T>();
        }

        internal static float[,]? CreateMotionMask(PixelInfo[] pixelsInfo, int standardNumber, int motionFilterStrength)
        {
            if (motionFilterStrength <= 0)
            {
                return null;
            }

            const float alphaMotionPerStrengthUnit = 12f / 100f;
            var strength = Math.Clamp(motionFilterStrength, 1, 100);
            return MotionMask.BuildMotionMask(pixelsInfo, standardNumber, strength * alphaMotionPerStrengthUnit, 3f);
        }

        private IRadianceMap CreateRadianceMap(ToneMapperSettings? toneMapperSettings)
        {
            if (this.context != null)
            {
                return new RadianceMapGpu(this.context, toneMapperSettings);
            }

            return SystemHelper.UseAvx ? new RadianceMapSIMD(toneMapperSettings) : new RadianceMap(toneMapperSettings);
        }

        internal static double[] GSolve(
            PixelInfo[] pixelInfo,
            int smoothFactor,
            int channel,
            float[] lutWeight,
            bool useAvx,
            bool[]? includedSamples = null)
        {
            const int responseValueCount = Const.zMax + 1;
            var exposureCount = pixelInfo.Length;
            var sampleCount = pixelInfo[0].Rgb[0].Length;

            var normal = new double[responseValueCount * responseValueCount];
            var rhs = new double[responseValueCount];
            var sampleWeightsByZ = new double[responseValueCount];
            var sampleLogByZ = new double[responseValueCount];
            var activeZ = new int[exposureCount];

            for (var sample = 0; sample < sampleCount; sample++)
            {
                if (includedSamples is not null && !includedSamples[sample])
                {
                    continue;
                }

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
                        var rowOffset = z * responseValueCount;

                        normal[rowOffset + z] += wz;
                        rhs[z] += sampleLogByZ[z] - wz * sumWeightedLogTime * inverseSumWeight;

                        for (var j = 0; j < activeCount; j++)
                        {
                            var zz = activeZ[j];
                            normal[rowOffset + zz] -= wz * sampleWeightsByZ[zz] * inverseSumWeight;
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

            normal[(128 * responseValueCount) + 128] += 1;

            for (var i = 0; i < responseValueCount - 2; ++i)
            {
                var weight = smoothFactor * (double)lutWeight[i + 1];
                AddSmoothingRow(normal, responseValueCount, i, weight, -2 * weight, weight);
            }

            const double lambda = 1e-8;
            for (var i = 0; i < responseValueCount; i++)
            {
                normal[(i * responseValueCount) + i] += lambda;
            }

            return LeastSquares.SolveLinearSystem(normal, rhs, responseValueCount, useAvx);
        }

        internal static bool[] BuildResponseCurveInlierMask(
            PixelInfo[] pixelInfo,
            double[][] response,
            float[] lutWeight)
        {
            var sampleCount = pixelInfo[0].Rgb[0].Length;
            var exposureCount = pixelInfo.Length;
            var residuals = new double[sampleCount];

            for (var sample = 0; sample < sampleCount; sample++)
            {
                var weightedSquaredError = 0d;
                var totalWeight = 0d;
                for (var channel = 0; channel < Const.ChannelCount; channel++)
                {
                    var channelWeight = 0d;
                    var weightedIrradiance = 0d;
                    for (var exposure = 0; exposure < exposureCount; exposure++)
                    {
                        var z = pixelInfo[exposure].Rgb[channel][sample];
                        var weight = lutWeight[z];
                        var weightSquared = weight * weight;
                        if (weightSquared <= 1e-12)
                        {
                            continue;
                        }

                        weightedIrradiance +=
                            weightSquared * (response[channel][z] - pixelInfo[exposure].AvgLuminance);
                        channelWeight += weightSquared;
                    }

                    if (channelWeight <= 1e-12)
                    {
                        continue;
                    }

                    var logIrradiance = weightedIrradiance / channelWeight;
                    for (var exposure = 0; exposure < exposureCount; exposure++)
                    {
                        var z = pixelInfo[exposure].Rgb[channel][sample];
                        var weight = lutWeight[z];
                        var weightSquared = weight * weight;
                        if (weightSquared <= 1e-12)
                        {
                            continue;
                        }

                        var error = response[channel][z] -
                                    pixelInfo[exposure].AvgLuminance -
                                    logIrradiance;
                        weightedSquaredError += weightSquared * error * error;
                        totalWeight += weightSquared;
                    }
                }

                residuals[sample] = totalWeight > 1e-12
                    ? Math.Sqrt(weightedSquaredError / totalWeight)
                    : double.PositiveInfinity;
            }

            var finiteResiduals = residuals.Where(double.IsFinite).Order().ToArray();
            if (finiteResiduals.Length == 0)
            {
                return Enumerable.Repeat(true, sampleCount).ToArray();
            }

            var median = Median(finiteResiduals);
            var deviations = finiteResiduals
                .Select(value => Math.Abs(value - median))
                .Order()
                .ToArray();
            var medianAbsoluteDeviation = Median(deviations);
            var threshold = Math.Max(0.02d, median + (3d * 1.4826d * medianAbsoluteDeviation));
            var result = residuals.Select(value => double.IsFinite(value) && value <= threshold).ToArray();

            var minimumInlierCount = Math.Min(sampleCount, Math.Max(64, (int)Math.Ceiling(sampleCount * 0.7d)));
            if (result.Count(inlier => inlier) < minimumInlierCount)
            {
                Array.Fill(result, false);
                foreach (var index in Enumerable.Range(0, sampleCount)
                             .OrderBy(index => residuals[index])
                             .Take(minimumInlierCount))
                {
                    result[index] = true;
                }
            }

            return result;
        }

        private static double Median(double[] sorted)
        {
            if (sorted.Length == 0)
            {
                return 0d;
            }

            var middle = sorted.Length / 2;
            return sorted.Length % 2 == 1
                ? sorted[middle]
                : (sorted[middle - 1] + sorted[middle]) * 0.5d;
        }

        private static void AddSmoothingRow(
            double[] normal,
            int matrixSize,
            int startIndex,
            double left,
            double middle,
            double right)
        {
            AddSymmetric(normal, matrixSize, startIndex, startIndex, left * left);
            AddSymmetric(normal, matrixSize, startIndex, startIndex + 1, left * middle);
            AddSymmetric(normal, matrixSize, startIndex, startIndex + 2, left * right);
            AddSymmetric(normal, matrixSize, startIndex + 1, startIndex + 1, middle * middle);
            AddSymmetric(normal, matrixSize, startIndex + 1, startIndex + 2, middle * right);
            AddSymmetric(normal, matrixSize, startIndex + 2, startIndex + 2, right * right);
        }

        private static void AddSymmetric(double[] matrix, int matrixSize, int row, int column, double value)
        {
            matrix[(row * matrixSize) + column] += value;
            if (row != column)
            {
                matrix[(column * matrixSize) + row] += value;
            }
        }




        #endregion
    }
}
