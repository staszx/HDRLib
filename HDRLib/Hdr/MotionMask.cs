// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Hdr;

using Interfaces;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Intrinsics;
using HDRLib.MathUtils;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Debevec;
using static System.Net.Mime.MediaTypeNames;

internal static class MotionMask
{
    #region Methods


    public static unsafe float[,] BuildMotionMask(
     PixelInfo[] pixelInfo,
     int standardNumber,
     float alphaMotion = 6f,
     float gamma = 2f)
    {
        var reference = pixelInfo[standardNumber];
        int w = reference.Image.Width;
        int h = reference.Image.Height;
        var lnT = pixelInfo.Select(i => i.AvgLuminance).ToArray();

        var result = new float[h, w];

        Parallel.For(0, h, y =>
        {
            bool firstPass = true;
            var refRow = reference.LoadRow(y);

            for (int i = 0; i < pixelInfo.Length; i++)
            {
                if (i == standardNumber)
                    continue;

                var row = pixelInfo[i].LoadRow(y);

                float exposureFactor = MathF.Exp((float)lnT[i] - (float)lnT[standardNumber]);

                fixed (byte* pCur = row, pRef = refRow)
                {
                    for (int x = 0, wx = 0; x < row.Length; x += 3, wx++)
                    {
                        float l1 = LightFloat(pCur[x], pCur[x + 1], pCur[x + 2]);
                        float l2 = LightFloat(pRef[x], pRef[x + 1], pRef[x + 2]);
                        float l1norm = l1 / exposureFactor;
                        float l2norm = l2;
                        float diff = MathF.Abs(l1norm - l2norm);
                        float w = MathF.Exp(-diff * alphaMotion);
                        w = MathF.Pow(w, gamma);

                        if (firstPass)
                            result[y, wx] = w;
                        else
                            result[y, wx] *= w;
                    }
                }

                firstPass = false;
            }
        });

        return result;
    }


    // ---------------- utils ----------------------------------------------------

    private static float LightFloat(byte r, byte g, byte b)
        => (0.2126f * r + 0.7152f * g + 0.0722f * b) / 255f;


    // лог-релятивная радианс до GSolve
    // g(z) ? log(z) ДО вычисления точной кривой отклика
    private static float LogRadiancePreGSolve(float L, float lnT)
    {
        const float eps = 1e-6f;
        return MathF.Log(L + eps) - lnT;
    }

    public static unsafe void ApplyMotionMask(IImageProxy img, IImageProxy reference, float threshold =0.5f, float alphaMotion = 12f, float midTonePower = 2f)
    {
        var w = img.Width;
        var h = img.Height;


        for (var y = 0; y < h; y++)
        {
            var row = img.LoadRow(y);
            var refRow = reference.LoadRow(y);
            var width = row.Length;
            var changed = false;

            fixed (byte* pr = row, pRefRow = refRow)
            {
                for (int x = 0, wx=0; x < width; x += 3, wx++)
                {
                    var l = Light(pr[x], pr[x + 1], pr[x + 2]);
                    var lR = Light(pRefRow[x], pRefRow[x + 1], pRefRow[x + 2]);
                    var diff = MathF.Abs(l - lR);
                    var wMotion = MotionWeight(diff, alphaMotion);
                    var wHDR = MidToneWeight(l, midTonePower);
                    if ((wMotion * wHDR) <= threshold)
                    {
                        pr[x] = pRefRow[x];
                        pr[x + 1] = pRefRow[x + 1];
                        pr[x + 2] = pRefRow[x + 2];
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                img.SaveRow(y, row);
            }
        }
    }

    public static float ApplyMotionMask1Pixel(byte[] pixel, byte[] pixelStandard, float alphaMotion = 12f, float midTonePower = 2f)
    {
        var l = Light(pixel[0], pixel[1], pixel[2]);
        var lR = Light(pixelStandard[0], pixelStandard[1],pixelStandard[2]);
        var diff = MathF.Abs(l - lR);
        var wMotion = MotionWeight(diff, alphaMotion);
        var wHDR = MidToneWeight(l, midTonePower);
        return wMotion * wHDR;
    }


    private static float Light(byte r, byte g, byte b)=> (0.2126f * r + 0.7152f * g + 0.0722f * b) / 255f;
    
    private static float MotionWeight(float diff, float alpha = 12f, float gamma = 2)
    {
        var w = MathF.Exp(-diff * alpha);
        return MathF.Pow(w, gamma);
    }

    private static float MidToneWeight(float z, float power = 2f)
    {
        var t = MathF.Abs(z - 0.5f) * 2f;
        var w = 1f - MathF.Pow(t, power);
        return MathF.Max(0, w);
    }

    #endregion
}




internal static class MotionMaskSimd
{
    private static readonly Vector256<float> RCoef = Vector256.Create(0.2126f);
    private static readonly Vector256<float> GCoef = Vector256.Create(0.7152f);
    private static readonly Vector256<float> BCoef = Vector256.Create(0.0722f);
    private static readonly Vector256<float> Inv255 = Vector256.Create(1f / 255f);

    public static unsafe void ApplyMotionMask(
        IImageProxy img,
        IImageProxy reference,
        float threshold = 0.3f,
        float alphaMotion = 12f,
        float midTonePower = 2f)
    {
        int w = img.Width;
        int h = img.Height;

        var vThreshold = Vector256.Create(threshold);
        var vHalf = Vector256.Create(0.5f);
        var vTwo = Vector256.Create(2f);
        var vPower = Vector256.Create(midTonePower);
        var vAlpha = Vector256.Create(alphaMotion);

        for (int y = 0; y < h; y++)
        {
            byte[] row = img.LoadRow(y);
            byte[] refRow = reference.LoadRow(y);

            bool changed = false;
            int pxCount = row.Length / 3; // number of pixels

            fixed (byte* pRow = row, pRef = refRow)
            {
                for (var x= 0; x <= pxCount - 8; x += 8)
                {

                    byte* src = pRow + x * 3;
                    byte* srcR = pRef + x * 3;

                    Vector256<byte> rgb1 = Avx.LoadVector256(src);
                    Vector256<byte> rgb2 = Avx.LoadVector256(src + 32); // overread OK (we check boundaries by design)

                    Vector256<byte> rr1 = Avx.LoadVector256(srcR);
                    Vector256<byte> rr2 = Avx.LoadVector256(srcR + 32);

                    // unpack to ushort > float
                    var (r, g, b) = ExtractRgbFloat(rgb1, rgb2);
                    var (rR, gR, bR) = ExtractRgbFloat(rr1, rr2);

                    // --------------------------------
                    // LIGHT = (R*0.2126 + G*0.7152 + B*0.0722) / 255
                    // --------------------------------
                    var L = CalcLight(r, g, b);
                    var LR = CalcLight(rR, gR, bR);

                    // abs diff
                    var diff = L - LR;
                    diff = Abs(diff);

                    // ----------------------------
                    // wMotion = Exp(-diff * alpha)
                    // ----------------------------
                    var wMotion = AvxMath.Exp(Avx.Multiply(diff, vAlpha));

                    // ----------------------------
                    // wHDR = 1 ? |z ? 0.5|^p * 2
                    // ----------------------------
                    var d = Abs(Avx.Subtract(L, vHalf));  // |z?0.5|
                    d = Avx.Multiply(d, vTwo);           // *2
                    var pow = d * d;
                    var wHDR = Avx.Subtract(Vector256.Create(1f), pow);

                    // final weight
                    var wFinal = Avx.Multiply(wMotion, wHDR);

                    // compare to threshold
                    var mask = Avx.Compare(wFinal, vThreshold, FloatComparisonMode.OrderedLessThanOrEqualSignaling);

                    if (!mask.Equals(Vector256<float>.Zero))
                    {
                        changed = true;
                        // blend bytes from reference where mask=true
                        ApplyMask8Pixels(src, srcR, mask);
                    }
                }
            }

            if (changed)
                img.SaveRow(y, row);
        }
    }

    private static Vector256<float> Abs(Vector256<float> v)
    {
        // маска знака: 0x80000000
        var signMask = Vector256.Create(-0.0f); // биты знака = 1
        return Avx.AndNot(signMask, v); // очищаем бит знака -> abs(v)
    }

    // ------------------------------------------------------------
    // Extract R,G,B float vectors from 2?Vector256<byte>
    // ------------------------------------------------------------
    private unsafe static (Vector256<float> r, Vector256<float> g, Vector256<float> b)
        ExtractRgbFloat(Vector256<byte> v1, Vector256<byte> v2)
    {
        // Объединяем v1+v2 в один 64-байтовый массив.
        byte* tmp = stackalloc byte[64];
        Avx.Store(tmp, v1);
        tmp += 32;
        Avx.Store(tmp, v2);

        // Извлекаем ровно 8 пикселей RGB (24 байта)
        // Результат: r0..r7, g0..g7, b0..b7
        float* rf = stackalloc float[8];
        float* gf = stackalloc float[8];
        float* bf = stackalloc float[8];

        // Цикл из 8 пикселей — полностью безопасен и быстрый (развернёт JIT)
        for (int i = 0; i < 8; i++)
        {
            int o = i * 3;
            rf[i] = tmp[o];       // R
            gf[i] = tmp[o + 1];   // G
            bf[i] = tmp[o + 2];   // B
        }

        // Теперь загружаем в Vector256<float>
        var rVec = Avx.LoadVector256(rf);
        var gVec = Avx.LoadVector256(gf);
        var bVec = Avx.LoadVector256(bf);

        return (rVec, gVec, bVec);
    }

    private static Vector256<float> CalcLight(Vector256<float> r, Vector256<float> g, Vector256<float> b)
    {
        var sum = Avx.Add(Avx.Add(
            Avx.Multiply(r, RCoef),
            Avx.Multiply(g, GCoef)),
            Avx.Multiply(b, BCoef));

        return Avx.Multiply(sum, Inv255);
    }

    


    // ------------------------------------------------------------
    // Apply pixel mask
    // ------------------------------------------------------------
    private static unsafe void ApplyMask8Pixels(byte* dst, byte* src, Vector256<float> mask)
    {
        // reinterpret mask float > byte mask
        var maskBytes = Avx2.CompareEqual(FloatMaskToByteMask(mask), Vector256<byte>.Zero);
        // then blend bytes
        var dstVec = Avx.LoadVector256(dst);
        var srcVec = Avx.LoadVector256(src);

        var blended = Avx2.BlendVariable(dstVec, srcVec, maskBytes);
        Avx.Store(dst, blended);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> FloatMaskToByteMask(Vector256<float> maskFloat)
    {
        // maskFloat содержит результат сравнения: элементы либо 0xFFFFFFFF, либо 0x00000000 как float
        // Конвертируем float > int > byte (0 или 255)

        // Преобразуем float > int (все биты сохраняются)
        Vector256<int> mi = maskFloat.AsInt32();

        // Приводим знаковое int > byte (при 0xFFFFFFFF > 255, при 0 > 0)
        // Делается через усечение (saturate) — Intel intrinsic Pack/Shuffle.

        // Сжимаем 8 int > 8 short
        Vector256<short> shorts = Avx2.PackSignedSaturate(mi, Vector256<int>.Zero);

        // Сжимаем 8 short > 8 byte
        Vector256<byte> bytes = Avx2.PackUnsignedSaturate(shorts, Vector256<short>.Zero);

        // Теперь первые 8 байтов — это настоящая маска.
        // Но BlendVariable требует 32-байтовый вектор.
        // Расширим байты (дублируем) до 32 bytes = 8 значений по 4 раза.
        Vector256<byte> expanded = Avx2.Shuffle(bytes, Vector256.Create(
            (byte)0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3,
            4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7
        ));

        return expanded;
    }
}
