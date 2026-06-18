// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Post;

using System.Runtime.CompilerServices;
using Image;
using PostProcessors;
using ToneMapping;

public sealed class LabPostProcessor : IPostProcessor
{
    #region Constants

    private const float LabOffset = 16f;
    private const float LabDivisor = 116f;
    private const float LabToXScale = 0.95047f;
    private const float LabToYScale = 1.00000f;
    private const float LabToZScale = 1.08883f;
    private const float LabPivot = 0.008856f;
    private const float LabInvFactor = 7.787f;
    private const float SrgbLinearThreshold = 0.0031308f;
    private const float SrgbLinearFactor = 12.92f;
    private const float SrgbGammaScale = 1.055f;
    private const float SrgbGammaOffset = 0.055f;
    private const float SrgbGamma = 2.4f;
    private const double LinearThreshold = 0.04045;
    private const double LinearDivisor = 12.92;
    private const double LinearOffset = 0.055;
    private const double LinearScale = 1.055;

    private const float MinRangeEpsilon = 1e-6f;
    private const float ShadowsFrom = 0.0f;
    private const float ShadowsTo = 0.35f;
    private const float MidCenter = 0.5f;
    private const float MidGaussianDivisor = 0.12f;
    private const float HighlightsFrom = 0.55f;
    private const float HighlightsTo = 1.0f;
    private const float ShadowsStrength = 12f;
    private const float MidtonesStrength = 6f;
    private const float HighlightsStrength = 12f;
    private const float ContrastIdentity = 1.0f;
    private const float ContrastPivot = 50f;
    private const float ContrastScale = 50f;
    private const float ContrastStrength = 40f;
    private const float GrayMaskStart = 0.05f;
    private const float GrayMaskEnd = 0.25f;
    private const float WhitePointL = 100f;
    private const float ExposureMin = 0.85f;
    private const float ExposureMax = 1.25f;

    #endregion

    #region Fields

    private PostProcessSettings settings;

    #endregion

    #region Constructors

    public LabPostProcessor(PostProcessSettings settings)
    {
        this.settings = settings;
    }

    #endregion

    #region Methods

    #region LAB > RGB

    public static Rgb LabToRgb255(in Lab lab)
    {
        LabToRgb01(lab.L, lab.A, lab.B, out var r, out var g, out var b);
        return new Rgb(r, g, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LabToRgb01(float l, float a, float b, out float r, out float g, out float blue)
    {
        var fy = (l + LabOffset) / LabDivisor;
        var fx = a / 500f + fy;
        var fz = fy - b / 200f;

        var xr = InverseLabCurve(fx);
        var yr = InverseLabCurve(fy);
        var zr = InverseLabCurve(fz);

        var x = xr * LabToXScale;
        var y = yr * LabToYScale;
        var z = zr * LabToZScale;

        var rl = 3.2404542f * x - 1.5371385f * y - 0.4985314f * z;
        var gl = -0.9692660f * x + 1.8760108f * y + 0.0415560f * z;
        var bl = 0.0556434f * x - 0.2040259f * y + 1.0572252f * z;

        r = ToSrgb01(rl);
        g = ToSrgb01(gl);
        blue = ToSrgb01(bl);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float InverseLabCurve(float t)
    {
        var t3 = t * t * t;
        return t3 > LabPivot ? t3 : (t - LabOffset / LabDivisor) / LabInvFactor;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ToSrgb01(float c)
    {
        return c <= SrgbLinearThreshold ? SrgbLinearFactor * c : SrgbGammaScale * MathF.Pow(c, 1f / SrgbGamma) - SrgbGammaOffset;
    }

    #endregion

    #region RGB > LAB

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ToLinear(double c)
    {
        return c <= LinearThreshold ? c / LinearDivisor : Math.Pow((c + LinearOffset) / LinearScale, SrgbGamma);
    }

    public static Lab Rgb01ToLab(in Rgb rgb)
    {
        RgbToLab(rgb.Red, rgb.Green, rgb.Blue, out var l, out var a, out var b);
        return new Lab(l, a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RgbToLab(float red, float green, float blue, out float l, out float a, out float b)
    {
        var r = ToLinear(red);
        var g = ToLinear(green);
        var bLin = ToLinear(blue);

        var x = r * 0.4124564 + g * 0.3575761 + bLin * 0.1804375;
        var y = r * 0.2126729 + g * 0.7151522 + bLin * 0.0721750;
        var z = r * 0.0193339 + g * 0.1191920 + bLin * 0.9503041;

        var nx = x / LabToXScale;
        var ny = y / LabToYScale;
        var nz = z / LabToZScale;

        var fx = LabCurve(nx);
        var fy = LabCurve(ny);
        var fz = LabCurve(nz);

        l = (float)(116 * fy - 16);
        a = (float)(500 * (fx - fy));
        b = (float)(200 * (fy - fz));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double LabCurve(double t)
    {
        return t > LabPivot ? Math.Pow(t, 1.0 / 3.0) : LabInvFactor * t + 16.0 / 116.0;
    }

    #endregion

    #region LAB Post-Processing

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SmoothStep(float a, float b, float x)
    {
        x = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return x * x * (3 - 2 * x);
    }

    public unsafe void ApplyInPlace(Image<Rgb> image)
    {
        var len = image.Length;
        if (len == 0)
        {
            return;
        }

        var labs = new Lab[len];
        var pixels = image.Pixels;
        var s = this.settings;

        var minL = float.MaxValue;
        var maxL = float.MinValue;

        fixed (Rgb* pxPtr = pixels)
        fixed (Lab* labPtr = labs)
        {
            for (var i = 0; i < len; i++)
            {
                var p = pxPtr[i];
                RgbToLab(p.Red, p.Green, p.Blue, out var l, out var a, out var b);
                labPtr[i] = new Lab(l, a, b);

                if (l < minL)
                {
                    minL = l;
                }

                if (l > maxL)
                {
                    maxL = l;
                }
            }

            var range = MathF.Max(maxL - minL, MinRangeEpsilon);
            var invRange = 1.0f / range;
            var exposure = Math.Clamp(MathF.Pow(2f, s.Exposure), ExposureMin, ExposureMax);
            var brightness = s.Brightness;
            var shadowsDelta = (s.Shadows - 1f) * ShadowsStrength;
            var midtonesDelta = (s.Midtones - 1f) * MidtonesStrength;
            var highlightsDelta = (s.Highlights - 1f) * HighlightsStrength;
            var contrastDelta = s.Contrast - 1f;
            var vibranceDelta = s.Vibrance - 1f;
            var applyContrast = MathF.Abs(s.Contrast - ContrastIdentity) > MinRangeEpsilon;

            for (var i = 0; i < len; i++)
            {
                var lab = labPtr[i];
                var l = lab.L;
                var a = lab.A;
                var b = lab.B;

               var ln = (l - minL) * invRange;

                l *= exposure;
                l *= brightness;

                var shMask = SmoothStep(ShadowsFrom, ShadowsTo, 1 - ln);
                l += shMask * shadowsDelta;

                var centeredLn = ln - MidCenter;
                var midMask = MathF.Exp(-(centeredLn * centeredLn) / MidGaussianDivisor);
                l += midMask * midtonesDelta;

                var hlMask = SmoothStep(HighlightsFrom, HighlightsTo, ln);
                l -= hlMask * highlightsDelta;

                if (applyContrast)
                {
                    var x = (l - ContrastPivot) / ContrastScale;
                    var sCurve = x * (1 - MathF.Abs(x));
                    l += sCurve * contrastDelta * ContrastStrength;
                }

                var chroma = MathF.Sqrt(a * a + b * b);
                var grayMask = SmoothStep(GrayMaskStart, GrayMaskEnd, chroma);
                var vibranceScale = 1 + grayMask * vibranceDelta;
                a *= vibranceScale;
                b *= vibranceScale;

                l = Math.Clamp(l, 0, WhitePointL);
                labPtr[i] = new Lab(l, a, b);
            }

            for (var i = 0; i < len; i++)
            {
                var lab = labPtr[i];
                LabToRgb01(lab.L, lab.A, lab.B, out var r, out var g, out var b);
                pxPtr[i] = new Rgb(r, g, b);
            }
        }
    }

    #endregion
    #endregion
}
