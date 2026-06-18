// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.MathUtils
{
    using System.Numerics;
    using System.Runtime.CompilerServices;

    /// <summary>
/// Provides methods for solving linear least‑squares problems using various algorithms.
/// </summary>
public class LeastSquares
    {
        #region Methods

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        /// <summary>
/// Solves a linear least‑squares problem using QR decomposition.
/// </summary>
/// <param name="a">Matrix of coefficients (rows × columns).</param>
/// <param name="b">Right‑hand side vector.</param>
/// <returns>Solution vector, or null if input is invalid.</returns>
public static double[] LinearLeastSquares(double[][] a, double[] b)
        {

            if (a == null || a.Length == 0)
            {
                return null;
            }

            var lines = a.Length;
            var columns = a[0].Length;
            var q = MathHelper.Copy(a);
            var r = MathHelper.Initialize2DArray<double>(columns, columns);
            var x =  GC.AllocateUninitializedArray<double>(columns);
            double s;

            QRdecomposition(q, r);
            for (var i = 0; i < columns; i++)
            {
                s = 0.0F;
                for (var j = 0; j < lines; j++)
                {
                    s += q[j][i] * b[j];
                }

                x[i] = s;
            }

            for (var i = columns - 1; 0 <= i; i--)
            {
                s = r[i][i];
                if (s  == 0.0F)
                {
                    x[i] = 0.0F;
                }
                else
                {
                    x[i] /= s;
                }

                for (var j = i - 1; 0 <= j; j--)
                {
                    x[j] -= r[j][i] * x[i];
                }
            }

            return x;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        /// <summary>
/// Performs QR decomposition on matrix <c>q</c> and stores the result in <c>r</c>.
/// </summary>
/// <param name="q">Input matrix to decompose (will be modified).</param>
/// <param name="r">Output upper‑triangular matrix.</param>
public static void QRdecomposition(double[][] q, double[][] r)
        {
            var lines = q.Length;
            var columns = q[0].Length;
            var a = MathHelper.Copy(q);
            double s;
            for (var j = 0; j < columns; j++)
            {
                for (var k = 0; k < j; k++)
                {
                    s = 0.0F;
                    for (var i = 0; i < lines; i++)
                    {
                        s += a[i][j] * q[i][k];
                    }

                    for (var i = 0; i < lines; i++)
                    {
                        q[i][j] -= s * q[i][k];
                    }
                }

                s = 0.0F;
                for (var i = 0; i < lines; i++)
                {
                    s += q[i][j] * q[i][j];
                }

                if (s  == 0.0F)
                {
                    s = 0.0F;
                }
                else
                {
                    s = 1.0F / Math.Sqrt(s);
                }

                for (var i = 0; i < lines; i++)
                {
                    q[i][j] *= s;
                }
            }

            for (var i = 0; i < columns; i++)
            {
                for (var j = 0; j < i; j++)
                {
                    r[i][j] = 0.0F;
                }

                for (var j = i; j < columns; j++)
                {
                    r[i][j] = 0.0F;
                    for (var k = 0; k < lines; k++)
                    {
                        r[i][j] += q[k][i] * a[k][j];
                    }
                }
            }
        }


        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        /// <summary>
/// Computes a least‑squares solution using AVX‑accelerated operations.
/// </summary>
/// <param name="a">Coefficient matrix.</param>
/// <param name="b">Right‑hand side vector.</param>
/// <param name="rowCount">Number of rows to process.</param>
/// <returns>Solution vector.</returns>
public static double[] FastLeastSquaresAvx(double[][] a, double[] b, int rowCount)
        {
            int m = rowCount;
            int n = a[0].Length;

            // ATA и ATb
            var ata = new double[n][];
            for (int i = 0; i < n; i++)
                ata[i] = GC.AllocateUninitializedArray<double>(n);
            var atb = GC.AllocateUninitializedArray<double>(n);

            int vectorWidth = Vector<double>.Count;

            for (int row = 0; row < m; row++)
            {
                double[] ai = a[row];
                double bi = b[row];

                // --- ATb ---
                int j = 0;
                for (; j <= n - vectorWidth; j += vectorWidth)
                {
                    var vec = new Vector<double>(ai, j);
                    var vecB = vec * bi;
                    for (int t = 0; t < vectorWidth; t++)
                        atb[j + t] += vecB[t];
                }
                for (; j < n; j++)
                    atb[j] += ai[j] * bi;

                // --- ATA верхний треугольник ---
                for (int iCol = 0; iCol < n; iCol++)
                {
                    double aVal = ai[iCol];
                    int k = iCol;
                    for (; k <= n - vectorWidth; k += vectorWidth)
                    {
                        var vecAi = new Vector<double>(ai, k);
                        var vecMul = vecAi * aVal;
                        for (int t = 0; t < vectorWidth; t++)
                            ata[iCol][k + t] += vecMul[t];
                    }
                    for (; k < n; k++)
                        ata[iCol][k] += aVal * ai[k];
                }
            }

            // Симметризация
            for (int i = 0; i < n; i++)
                for (int j = 0; j < i; j++)
                    ata[i][j] = ata[j][i];

            // Регуляризация λ для устойчивости
            const double lambda = 1e-8;
            for (int i = 0; i < n; i++)
                ata[i][i] += lambda;

            // Решаем систему Gaussian elimination
            return SolveGaussian(ata, atb);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        /// <summary>
/// Computes a least‑squares solution without AVX optimisations.
/// </summary>
/// <param name="a">Coefficient matrix.</param>
/// <param name="b">Right‑hand side vector.</param>
/// <param name="rowCount">Number of rows to process.</param>
/// <returns>Solution vector.</returns>
public static double[] FastLeastSquares(double[][] a, double[] b, int rowCount)
        {
            int m = rowCount;
            int n = a[0].Length;

            var ata = new double[n][];
            for (int i = 0; i < n; i++)
                ata[i] = new double[n];
            var atb = new double[n];

            // --- Aᵀ * A и Aᵀ * b ---
            for (int i = 0; i < m; i++)
            {
                var ai = a[i];
                double bi = b[i];
                for (int j = 0; j < n; j++)
                {
                    double aij = ai[j];
                    if (aij == 0.0) continue;
                    atb[j] += aij * bi;
                    var rowj = ata[j];
                    for (int k = j; k < n; k++)
                        rowj[k] += aij * ai[k];
                }
            }

            // Симметризация
            for (int i = 0; i < n; i++)
                for (int j = 0; j < i; j++)
                    ata[i][j] = ata[j][i];

            // --- Регуляризация для устойчивости ---
            const double lambda = 1e-8;
            for (int i = 0; i < n; i++)
                ata[i][i] += lambda;

            // --- Решение AᵀA * x = Aᵀb через обратный ход Гаусса ---
            return SolveGaussian(ata, atb);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static double[] SolveLinearSystem(double[][] a, double[] b)
        {
            return SolveGaussian(a, b);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static double[] SolveGaussian(double[][] a, double[] b)
        {
            int n = b.Length;
            var x = new double[n];

            // Прямой ход
            for (int i = 0; i < n; i++)
            {
                // Находим максимальный элемент для стабильности
                int maxRow = i;
                double maxVal = Math.Abs(a[i][i]);
                for (int k = i + 1; k < n; k++)
                {
                    double val = Math.Abs(a[k][i]);
                    if (val > maxVal)
                    {
                        maxVal = val;
                        maxRow = k;
                    }
                }

                // Меняем строки местами
                if (maxRow != i)
                {
                    var tempRow = a[i];
                    a[i] = a[maxRow];
                    a[maxRow] = tempRow;

                    double tmp = b[i];
                    b[i] = b[maxRow];
                    b[maxRow] = tmp;
                }

                double diag = a[i][i];
                if (Math.Abs(diag) < 1e-15)
                    diag = 1e-15;

                for (int j = i; j < n; j++)
                    a[i][j] /= diag;
                b[i] /= diag;

                for (int k = i + 1; k < n; k++)
                {
                    double factor = a[k][i];
                    if (factor == 0.0) continue;
                    for (int j = i; j < n; j++)
                        a[k][j] -= factor * a[i][j];
                    b[k] -= factor * b[i];
                }
            }

            // Обратный ход
            for (int i = n - 1; i >= 0; i--)
            {
                double sum = b[i];
                for (int j = i + 1; j < n; j++)
                    sum -= a[i][j] * x[j];
                x[i] = sum;
            }

            return x;
        }


        #endregion
    }
}
