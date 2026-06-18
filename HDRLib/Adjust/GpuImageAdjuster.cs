// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Adjust
{

    using Gpu;
    using ILGPU;
    using ILGPU.Algorithms;
    using ILGPU.Runtime;
    using System.Numerics;
    using Hdr;
    using HDRLib.Image;

    public class GpuImageAdjuster
    {

        private readonly Accelerator accelerator;

        public GpuImageAdjuster(GpuContext gpuContext)
        {
            this.accelerator = gpuContext.Accelerator;
        }

        public void OptimizeLabContrast(ArrayView1D<Lab, Stride1D.Dense> input)
        {
            var hist = BuildHistogram(input);
            var minMax = FindMinMax(hist);
            ApplyAutoContrast(input, Math.Abs(minMax[0]), Math.Abs(minMax[1]));
        }

        public void AutoTone(ArrayView1D<Lab, Stride1D.Dense> input)
        {
            var pixelCount = input.IntLength;

            // 1?? Обнуляем буфер для сумм a/b
            using var abSumGpu = accelerator.Allocate1D<float>(2);
        //    abSumGpu.MemSetToZero();

            // 2?? Вычисляем суммы a/b
            var abMeanKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Lab, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>>(ComputeABMeanKernel);
            abMeanKernel(pixelCount, input, abSumGpu);
            accelerator.Synchronize();

            // Получаем средние
            var abSum = abSumGpu.GetAsArray1D();
            var meanA = abSum[0] * 1f/ (pixelCount);
            var meanB = abSum[1] * 1f/ (pixelCount);
           // Console.WriteLine($"meanA={meanA}, meanB={meanB}");

            var hist = BuildHistogram(input);
            var minMax = FindMinMax(hist);

            // 4?? Применяем AutoTone
            var autoToneKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<Lab>, float, float, float, float>(AutoToneKernel);
            autoToneKernel(pixelCount, input, minMax[0], minMax[1], meanA, meanB);
            accelerator.Synchronize();
        }

        public void AutoColor(ArrayView1D<Lab, Stride1D.Dense> input)
        {
            var pixelCount = input.IntLength;

            // 1?? Обнуляем буфер для суммы A/B
            using var abSumGpu = accelerator.Allocate1D<float>(2);
            abSumGpu.MemSetToZero();

            // 2?? Считаем сумму A/B
            var sumABKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Lab, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>>(SumABKernel);
            sumABKernel(pixelCount, input, abSumGpu);
            accelerator.Synchronize();

            // Получаем средние значения на CPU
            var abSum = abSumGpu.GetAsArray1D();
            var meanA = abSum[0] / pixelCount;
            var meanB = abSum[1] / pixelCount;

            // 3?? Применяем Auto Color (сдвигаем A/B)
            var autoColorKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Lab, Stride1D.Dense>, float, float>(AutoColorKernel);
            autoColorKernel(pixelCount, input, meanA, meanB);
            accelerator.Synchronize();
        }

        public void Vibrance(ArrayView1D<Lab, Stride1D.Dense> input)
        {
            VibranceStats stats;
            using (var statsBuf = accelerator.Allocate1D<VibranceStats>(1))
            {
                statsBuf.MemSetToZero();

                var statsKernel = accelerator.LoadAutoGroupedStreamKernel<
                    Index1D, ArrayView<Lab>, ArrayView<VibranceStats>>(VibranceStatsKernel);

                statsKernel(input.IntLength, input, statsBuf.View);
                accelerator.Synchronize();

                stats = statsBuf.GetAsArray1D()[0];
            }

            float n = stats.Count;
            float Lmean = stats.Lsum / n;
            float Lstd = XMath.Sqrt(stats.Lsum2 / n - Lmean * Lmean);
            float chromaMean = stats.Csum / n;
            float chromaMax = stats.Cmax;

            // Эвристика, как выше:
            float midToneCenter = Lmean/100f;
            float midToneWidth = XMath.Clamp(Lstd * 2f, 25f, 50f)/100f;
            float minChroma = (chromaMean * 0.3f)/100f;
            float maxChroma = XMath.Min(chromaMean * 1.5f, chromaMax)/100f;
            float intensity = 1.3f;// (chromaMean < 20f ? 8f : chromaMean < 40f ? 2.5f : 1.50f);
            Console.WriteLine($"Lmean={Lmean}, midToneWidth={midToneWidth} minChroma={minChroma} maxChroma={maxChroma}");
            var vibranceKernel = accelerator.LoadAutoGroupedStreamKernel<
                Index1D, ArrayView<Lab>, float, float, float, float, float>(VibranceKernel);

            vibranceKernel(
                input.IntLength, 
                input,
                intensity,
                minChroma,
                maxChroma,
                midToneCenter,
                midToneWidth);
            accelerator.Synchronize();
        }

        private float[] FindMinMax(MemoryBuffer1D<int, Stride1D.Dense> hist)
        {
            using var totalGpu = accelerator.Allocate1D<int>(1);
            var sumKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>>(SumHistogramKernel);
            sumKernel(1,hist.View, totalGpu.View);
            accelerator.Synchronize();
            var totalCount = totalGpu.GetAsArray1D();
            using var minMax = accelerator.Allocate1D<float>(2);
            var kernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<int, Stride1D.Dense>, int, float, ArrayView1D<float, Stride1D.Dense>>(FindMinMaxKernel); kernel(1,
                hist.View,
                totalCount[0],
                0.5f,
                minMax
            );

            accelerator.Synchronize();

            // получить назад 2 значения
            float[] result = minMax.GetAsArray1D();
            return result;
        }

        public MemoryBuffer1D<int, Stride1D.Dense> BuildHistogram(ArrayView1D<Lab, Stride1D.Dense> input)
        {
            var gpuHist = this.accelerator.Allocate1D<int>(1001);
            gpuHist.MemSetToZero();
            var kernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<Lab>, ArrayView<int>>(HistogramKernel);
            kernel(input.IntLength, input, gpuHist.View);
            accelerator.Synchronize();
            return gpuHist;
        }

        public static void SumHistogramKernel(
            Index1D index,
            ArrayView1D<int, Stride1D.Dense> hist,
            ArrayView1D<int, Stride1D.Dense> result)
        {
            int i = Grid.GlobalIndex.X;
            if (i >= hist.Length) return;

            Atomic.Add(ref result[0], hist[i]);
        }

        private static void FindMinMaxKernel(
            Index1D index,
            ArrayView1D<int, Stride1D.Dense> hist,     // размер 1001
            int totalCount,
            float clip,
            ArrayView1D<float, Stride1D.Dense > result // result[0] = Lmin, result[1] = Lmax
        )
        {
            // kernel must run as single thread
            if (Grid.IdxX != 0 || Group.IdxX != 0)
            {
                return;
            }

            var clipCount = (int)(totalCount * clip);

            // --- Lmin ---
            var sum = 0;
            var LminIndex = 0;

            while (LminIndex < hist.Length && sum < clipCount)
            {
                sum += hist[LminIndex];
                LminIndex++;
            }

            // --- Lmax ---
            sum = 0;
            var LmaxIndex = hist.Length - 1;

            while (LmaxIndex >= 0 && sum < clipCount)
            {
                sum += hist[LmaxIndex];
                LmaxIndex--;
            }

            // Convert to L with step 0.1
            result[0] = LminIndex / 10.0f;
            result[1] = LmaxIndex / 10.0f;
        }


        public void ApplyAutoContrast(ArrayView1D<Lab, Stride1D.Dense> pixels, float lMin, float lMax)
        {
            var kernel = accelerator.LoadAutoGroupedStreamKernel<
                Index1D,
                ArrayView1D<Lab, Stride1D.Dense>,
                float,
                float>(ApplyAutoContrastKernel);
            kernel(pixels.IntLength, pixels, lMin, lMax );
            this.accelerator.Synchronize();
        }
     

        private static void HistogramKernel(
            Index1D index,
            ArrayView<Lab> pixels,
            ArrayView<int> hist)
        {
            float L = pixels[index].L;
            int bin = (int)(L * 10.0);     

            if ((uint)bin < 1001)
            {
                Atomic.Add(ref hist[bin], 1);
            }
        }

        private static void ApplyAutoContrastKernel(
            Index1D index,
            ArrayView1D<Lab, Stride1D.Dense> pixels,
            float Lmin,
            float Lmax)
        {
            var scale = 100.0f / ((Lmax - Lmin) + 1e-12f);

            var p = pixels[index];
            var L = (p.L - Lmin) * scale;
         //   L = 50 +1.2f * (L - 50);
         //   L *= 0.1f;
            p.L = XMath.Clamp(L, 0.0f, 100.0f);
            pixels[index] = p;
        }

        private static void ComputeABMeanKernel(
            Index1D index,
            ArrayView1D<Lab, Stride1D.Dense> pixels,
            ArrayView1D<float, Stride1D.Dense> result) // result[0] = sumA, result[1] = sumB
        {
            if (index >= pixels.Length)
                return;

            var p = pixels[index];

            Atomic.Add(ref result[0], p.A);
            Atomic.Add(ref result[1], p.B);
        }

        static void AutoToneKernel(
            Index1D index,
            ArrayView<Lab> pixels,
            float Lmin,
            float Lmax,
            float meanA,
            float meanB)
        {
            if (index >= pixels.Length)
            {
                return;
            }

            var p = pixels[index];

            // --- Растяжение L ---
            var scale = 100.0 / (Lmax - Lmin + 1e-12);
            var L = (float)((p.L - Lmin) * scale);
            L = XMath.Clamp(L, 0f, 100f);
       
            // --- Коррекция a/b ---
            var A = p.A - meanA;
            var B = p.B - meanB;

            pixels[index] = new Lab
            {
                L = L,
                A = A,
                B = B
            };
        }

        private static void SumABKernel(Index1D index, ArrayView1D<Lab, Stride1D.Dense> pixels, ArrayView1D<float, Stride1D.Dense> abSum) // abSum[0] = sumA, abSum[1] = sumB
        {
            if (index >= pixels.Length)
                return;

            var p = pixels[index];

            Atomic.Add(ref abSum[0], p.A);
            Atomic.Add(ref abSum[1], p.B);
        }

        static void AutoColorKernel(
            Index1D index,
            ArrayView1D<Lab, Stride1D.Dense> pixels,
            float meanA,
            float meanB)
        {
            if (index >= pixels.Length)
                return;

            var p = pixels[index];

            // Сдвигаем A и B для нейтрализации среднего цвета
            var A = p.A - meanA;
            var B = p.B - meanB;

            pixels[index] = new Lab { L = p.L, A = A, B = B };
        }

        static void VibranceKernel(
            Index1D index,
            ArrayView<Lab> image,
            float intensity,
            float minChroma,     // ниже этого насыщенность не трогаем
            float maxChroma,     // выше — максимальное усиление
            float midToneCenter, // центр средних тонов (0..100)
            float midToneWidth)  // ширина диапазона средних тонов
        {
            var lab = image[index];

            float lNorm = lab.L/100f;
            float a = lab.A;
            float b = lab.B;

            // Вычисляем насыщенность
            float chroma = XMath.Sqrt(a * a + b * b);

            // Нормализуем
            float chromaNorm = chroma / 80f;

            // Мягкий переход между серыми и цветными
            float colorFactor =  GpuHelper.SmoothStep(minChroma, maxChroma, chromaNorm)/100f;

            // Ослабляем усиление в тенях и светах
            float lightFactor = 1f - XMath.Abs(lNorm - midToneCenter) / midToneWidth;
            lightFactor = XMath.Max(lightFactor, 0f);

            // Итоговый коэффициент усиления
            float factor = intensity + lightFactor * chromaNorm * colorFactor;

            lab.A = a * factor;
            lab.B = b * factor;

            image[index] = lab;
        }

        static void VibranceStatsKernel(
            Index1D index,
            ArrayView<Lab> image,
            ArrayView<VibranceStats> statsView)
        {
            var lab = image[index];
            float L = lab.L;
            float a = lab.A;
            float b = lab.B;
            float C = XMath.Sqrt(a * a + b * b);

            ref var stats = ref statsView[0];

            Atomic.Add(ref stats.Lsum, L);
            Atomic.Add(ref stats.Lsum2, L * L);
            Atomic.Add(ref stats.Csum, C);
            Atomic.Max(ref stats.Cmax, C);
            Atomic.Add(ref stats.Count, 1);
        }

        private static void ChromaHistogramKernel(
            Index1D index,
            ArrayView<Lab> pixels,
            ArrayView<int> hist,
            float maxC)
        {
            if (index >= pixels.Length)
                return;

            var p = pixels[index];
            float C = XMath.Sqrt(p.A * p.A + p.B * p.B);
            int bin = (int)(C / maxC * (hist.Length - 1));

            if (bin >= 0 && bin < hist.Length)
            {
                Atomic.Add(ref hist[bin], 1);
            }
        }

        private double FindCref(int[] hist, double percentile = 0.8, double maxC = 150.0)
        {
            int total = hist.Sum();
            int threshold = (int)(total * percentile);
            int acc = 0;
            for (int i = 0; i < hist.Length; i++)
            {
                acc += hist[i];
                if (acc >= threshold)
                {
                    double binValue = i / (double)(hist.Length - 1);
                    return binValue * maxC;
                }
            }
            return maxC * 0.5;
        }
    }
}
