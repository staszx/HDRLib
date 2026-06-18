// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Post;

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using HDRLib.MathUtils;
using PostProcessors;

public sealed class LabPostProcessorSIMD
{
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
    private const float LinearThreshold = 0.04045f;
    private const float LinearDivisor = 12.92f;
    private const float LinearOffset = 0.055f;
    private const float LinearScale = 1.055f;

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

    private readonly PostProcessSettings settings;

    public LabPostProcessorSIMD(PostProcessSettings settings)
    {
        this.settings = settings;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Clamp(Vector256<float> value, Vector256<float> min, Vector256<float> max)
    {
        return Vector256.Min(Vector256.Max(value, min), max);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> SmoothStep(Vector256<float> a, Vector256<float> b, Vector256<float> x)
    {
        var t = Clamp((x - a) / (b - a), Vector256<float>.Zero, Vector256<float>.One);
        return t * t * (Vector256.Create(3f) - (Vector256.Create(2f) * t));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> ToLinear(Vector256<float> c)
    {
        var threshold = Vector256.Create(LinearThreshold);
        var linear = c / Vector256.Create(LinearDivisor);
        var gammaBase = Vector256.Max((c + Vector256.Create(LinearOffset)) / Vector256.Create(LinearScale), Vector256.Create(1e-8f));
        var gamma = AvxMath.Pow(gammaBase, Vector256.Create(SrgbGamma));
        var mask = Avx.Compare(c, threshold, FloatComparisonMode.OrderedLessThanOrEqualNonSignaling);
        return Avx.BlendVariable(gamma, linear, mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> ToSrgb01(Vector256<float> c)
    {
        var threshold = Vector256.Create(SrgbLinearThreshold);
        var linear = Vector256.Create(SrgbLinearFactor) * c;
        var gammaBase = Vector256.Max(c, Vector256.Create(1e-8f));
        var gamma = (Vector256.Create(SrgbGammaScale) * AvxMath.Pow(gammaBase, Vector256.Create(1f / SrgbGamma))) - Vector256.Create(SrgbGammaOffset);
        var mask = Avx.Compare(c, threshold, FloatComparisonMode.OrderedLessThanOrEqualNonSignaling);
        return Avx.BlendVariable(gamma, linear, mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> LabCurve(Vector256<float> t)
    {
        var pivot = Vector256.Create(LabPivot);
        var linear = (Vector256.Create(LabInvFactor) * t) + Vector256.Create(LabOffset / LabDivisor);
        var gammaBase = Vector256.Max(t, Vector256.Create(1e-8f));
        var gamma = AvxMath.Pow(gammaBase, Vector256.Create(1f / 3f));
        var mask = Avx.Compare(t, pivot, FloatComparisonMode.OrderedGreaterThanNonSignaling);
        return Avx.BlendVariable(linear, gamma, mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> InverseLabCurve(Vector256<float> t)
    {
        var cube = t * t * t;
        var linear = (t - Vector256.Create(LabOffset / LabDivisor)) / Vector256.Create(LabInvFactor);
        var mask = Avx.Compare(cube, Vector256.Create(LabPivot), FloatComparisonMode.OrderedGreaterThanNonSignaling);
        return Avx.BlendVariable(linear, cube, mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Exp(Vector256<float> value)
    {
        return AvxMath.Exp(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RgbToLab(Vector256<float> r, Vector256<float> g, Vector256<float> b, out Vector256<float> l, out Vector256<float> a, out Vector256<float> bb)
    {
        var rl = ToLinear(r);
        var gl = ToLinear(g);
        var bl = ToLinear(b);

        var x = (rl * Vector256.Create(0.4124564f)) + (gl * Vector256.Create(0.3575761f)) + (bl * Vector256.Create(0.1804375f));
        var y = (rl * Vector256.Create(0.2126729f)) + (gl * Vector256.Create(0.7151522f)) + (bl * Vector256.Create(0.0721750f));
        var z = (rl * Vector256.Create(0.0193339f)) + (gl * Vector256.Create(0.1191920f)) + (bl * Vector256.Create(0.9503041f));

        var fx = LabCurve(x / Vector256.Create(LabToXScale));
        var fy = LabCurve(y / Vector256.Create(LabToYScale));
        var fz = LabCurve(z / Vector256.Create(LabToZScale));

        l = (Vector256.Create(116f) * fy) - Vector256.Create(16f);
        a = Vector256.Create(500f) * (fx - fy);
        bb = Vector256.Create(200f) * (fy - fz);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LabToRgb01(Vector256<float> l, Vector256<float> a, Vector256<float> b, out Vector256<float> r, out Vector256<float> g, out Vector256<float> bb)
    {
        var fy = (l + Vector256.Create(LabOffset)) / Vector256.Create(LabDivisor);
        var fx = (a / Vector256.Create(500f)) + fy;
        var fz = fy - (b / Vector256.Create(200f));

        var xr = InverseLabCurve(fx);
        var yr = InverseLabCurve(fy);
        var zr = InverseLabCurve(fz);

        var x = xr * Vector256.Create(LabToXScale);
        var y = yr * Vector256.Create(LabToYScale);
        var z = zr * Vector256.Create(LabToZScale);

        var rl = (Vector256.Create(3.2404542f) * x) - (Vector256.Create(1.5371385f) * y) - (Vector256.Create(0.4985314f) * z);
        var gl = (Vector256.Create(-0.9692660f) * x) + (Vector256.Create(1.8760108f) * y) + (Vector256.Create(0.0415560f) * z);
        var bl = (Vector256.Create(0.0556434f) * x) - (Vector256.Create(0.2040259f) * y) + (Vector256.Create(1.0572252f) * z);

        r = ToSrgb01(rl);
        g = ToSrgb01(gl);
        bb = ToSrgb01(bl);
    }

    public void ApplyInPlace(Vector256<float>[][] pixels)
    {
        var len = pixels[0].Length;
        if (len == 0)
        {
            return;
        }

        var lValues = GC.AllocateUninitializedArray<Vector256<float>>(len);
        var aValues = GC.AllocateUninitializedArray<Vector256<float>>(len);
        var bValues = GC.AllocateUninitializedArray<Vector256<float>>(len);

        var minLVec = Vector256.Create(float.MaxValue);
        var maxLVec = Vector256.Create(float.MinValue);

        for (var i = 0; i < len; i++)
        {
            RgbToLab(pixels[0][i], pixels[1][i], pixels[2][i], out var l, out var a, out var b);
            lValues[i] = l;
            aValues[i] = a;
            bValues[i] = b;
            minLVec = Vector256.Min(minLVec, l);
            maxLVec = Vector256.Max(maxLVec, l);
        }

        Span<float> minLane = stackalloc float[Vector256<float>.Count];
        Span<float> maxLane = stackalloc float[Vector256<float>.Count];
        minLVec.CopyTo(minLane);
        maxLVec.CopyTo(maxLane);

        var minL = minLane[0];
        var maxL = maxLane[0];
        for (var i = 1; i < minLane.Length; i++)
        {
            minL = MathF.Min(minL, minLane[i]);
            maxL = MathF.Max(maxL, maxLane[i]);
        }

        var s = this.settings;
        var range = MathF.Max(maxL - minL, MinRangeEpsilon);

        var minLVector = Vector256.Create(minL);
        var invRangeVector = Vector256.Create(1f / range);
        var exposure = Math.Clamp(MathF.Pow(2f, s.Exposure), ExposureMin, ExposureMax);
        var exposureVector = Vector256.Create(exposure);
        var brightnessVector = Vector256.Create(s.Brightness);
        var shadowsDelta = Vector256.Create((s.Shadows - 1f) * ShadowsStrength);
        var midtonesDelta = Vector256.Create((s.Midtones - 1f) * MidtonesStrength);
        var highlightsDelta = Vector256.Create((s.Highlights - 1f) * HighlightsStrength);
        var contrastDelta = Vector256.Create(s.Contrast - 1f);
        var contrastStrengthVector = Vector256.Create(MathF.Abs(s.Contrast - ContrastIdentity) > MinRangeEpsilon ? ContrastStrength : 0f);
        var vibranceDelta = Vector256.Create(s.Vibrance - 1f);

        var shadowFromV = Vector256.Create(ShadowsFrom);
        var shadowToV = Vector256.Create(ShadowsTo);
        var highlightFromV = Vector256.Create(HighlightsFrom);
        var highlightToV = Vector256.Create(HighlightsTo);
        var one = Vector256<float>.One;
        var midCenterV = Vector256.Create(MidCenter);
        var midDivV = Vector256.Create(MidGaussianDivisor);

        for (var i = 0; i < len; i++)
        {
            var l = lValues[i];
            var a = aValues[i];
            var b = bValues[i];

            var ln = (l - minLVector) * invRangeVector;
            l *= exposureVector;
            l *= brightnessVector;

            var shMask = SmoothStep(shadowFromV, shadowToV, one - ln);
            l += shMask * shadowsDelta;

            var centered = ln - midCenterV;
            var midMask = Exp(-(centered * centered) / midDivV);
            l += midMask * midtonesDelta;

            var hlMask = SmoothStep(highlightFromV, highlightToV, ln);
            l -= hlMask * highlightsDelta;

            var x = (l - Vector256.Create(ContrastPivot)) / Vector256.Create(ContrastScale);
            var sCurve = x * (one - Vector256.Abs(x));
            l += sCurve * contrastDelta * contrastStrengthVector;

            var chroma = Vector256.Sqrt((a * a) + (b * b));
            var grayMask = SmoothStep(Vector256.Create(GrayMaskStart), Vector256.Create(GrayMaskEnd), chroma);
            var vibranceScale = one + (grayMask * vibranceDelta);
            a *= vibranceScale;
            b *= vibranceScale;

            l = Clamp(l, Vector256<float>.Zero, Vector256.Create(WhitePointL));

            LabToRgb01(l, a, b, out var r, out var g, out var bb);
            pixels[0][i] = r;
            pixels[1][i] = g;
            pixels[2][i] = bb;
        }
    }
}
