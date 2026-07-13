// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Post;

using Adjust;
using Gpu;
using HDRLib.Image;
using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;
using PostProcessors;

public sealed class LabPostProcessorGpu
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
    private const float GrayMaskStart = 8f;
    private const float GrayMaskEnd = 60f;
    private const float WhitePointL = 100f;
    private const float ExposureMin = 0.85f;
    private const float ExposureMax = 1.25f;

    #endregion

    #region Fields

    private readonly GpuContext context;

    #endregion

    #region Constructors

    private readonly Accelerator accelerator;
    private readonly Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Lab, Stride1D.Dense>> rgbToLabKernel;
    private readonly Action<Index1D, ArrayView1D<Lab, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>> minMaxKernel;
    private readonly Action<Index1D, ArrayView1D<Lab, Stride1D.Dense>, float, float, float, float, float, float, float, float, float> adjustKernel;
    private readonly Action<Index1D, ArrayView1D<Lab, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>> labToRgbKernel;

    public LabPostProcessorGpu(GpuContext context)
    {
        this.context = context;
        this.accelerator = context.Accelerator;
        this.rgbToLabKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<Lab, Stride1D.Dense>>(RgbToLabKernel);
        this.minMaxKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Lab, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>>(FindLRangeKernel);
        this.adjustKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Lab, Stride1D.Dense>, float, float, float, float, float, float, float, float, float>(AdjustLabKernel);
        this.labToRgbKernel = this.accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<Lab, Stride1D.Dense>, ArrayView1D<Rgb, Stride1D.Dense>>(LabToRgbKernel);
    }

    #endregion

    #region Methods

 
    public void ApplyInPlace(ArrayView1D<Rgb, Stride1D.Dense> rgbBuffer, long length, PostProcessSettings settings)
    {
        using var labBuffer = this.accelerator.Allocate1D<Lab>(length);

        this.rgbToLabKernel((int)length, rgbBuffer, labBuffer.View);

        var minMax = new[] { float.MaxValue, float.MinValue };
        using var minMaxBuffer = this.accelerator.Allocate1D(minMax);
        this.minMaxKernel((int)length, labBuffer.View, minMaxBuffer.View);
        this.accelerator.Synchronize();
        minMaxBuffer.CopyToCPU(minMax);

        var minL = minMax[0];
        var range = XMath.Max(minMax[1] - minL, MinRangeEpsilon);
        var exposure = Math.Clamp(MathF.Pow(2f, settings.Exposure), ExposureMin, ExposureMax);

        this.adjustKernel(
            (int)length,
            labBuffer.View,
            minL,
            range,
            exposure,
            settings.Brightness,
            settings.Shadows,
            settings.Midtones,
            settings.Highlights,
            settings.Contrast,
            settings.Vibrance);

        this.labToRgbKernel((int)length, labBuffer.View, rgbBuffer);
        this.accelerator.Synchronize();
    }

    private static void RgbToLabKernel(Index1D idx, ArrayView1D<Rgb, Stride1D.Dense> rgb, ArrayView1D<Lab, Stride1D.Dense> lab)
    {
        var pixel = rgb[idx];

        var r = pixel.Red <= LinearThreshold
            ? pixel.Red / LinearDivisor
            : GpuHelper.Pow((pixel.Red + LinearOffset) / LinearScale, SrgbGamma);
        var g = pixel.Green <= LinearThreshold
            ? pixel.Green / LinearDivisor
            : GpuHelper.Pow((pixel.Green + LinearOffset) / LinearScale, SrgbGamma);
        var b = pixel.Blue <= LinearThreshold
            ? pixel.Blue / LinearDivisor
            : GpuHelper.Pow((pixel.Blue + LinearOffset) / LinearScale, SrgbGamma);

        var x = r * 0.4124564f + g * 0.3575761f + b * 0.1804375f;
        var y = r * 0.2126729f + g * 0.7151522f + b * 0.0721750f;
        var z = r * 0.0193339f + g * 0.1191920f + b * 0.9503041f;

        var nx = x / LabToXScale;
        var ny = y / LabToYScale;
        var nz = z / LabToZScale;

        static float LabCurve(float t)
        {
            return t > LabPivot ? GpuHelper.Pow(t, 1f / 3f) : LabInvFactor * t + LabOffset / LabDivisor;
        }

        var fx = LabCurve(nx);
        var fy = LabCurve(ny);
        var fz = LabCurve(nz);

        var l = LabDivisor * fy - LabOffset;
        var a = 500f * (fx - fy);
        var bOut = 200f * (fy - fz);

        lab[idx] = new Lab(l, a, bOut);
    }

    private static void FindLRangeKernel(Index1D idx, ArrayView1D<Lab, Stride1D.Dense> lab, ArrayView1D<float, Stride1D.Dense> minMax)
    {
        var l = lab[idx].L;
        Atomic.Min(ref minMax[0], l);
        Atomic.Max(ref minMax[1], l);
    }

    private static void AdjustLabKernel(
        Index1D idx,
        ArrayView1D<Lab, Stride1D.Dense> lab,
        float minL,
        float range,
        float exposure,
        float brightness,
        float shadows,
        float midtones,
        float highlights,
        float contrast,
        float vibrance)
    {
        var value = lab[idx];
        var l = value.L;
        var a = value.A;
        var b = value.B;

        var ln = (l - minL) / range;

        l *= exposure;
        l *= brightness;

        var shadowsDelta = (shadows - 1f) * ShadowsStrength;
        var shMask = GpuHelper.SmoothStep(ShadowsFrom, ShadowsTo, 1f - ln);
        l += shMask * shadowsDelta;

        var midtonesDelta = (midtones - 1f) * MidtonesStrength;
        var centeredLn = ln - MidCenter;
        var midMask = GpuHelper.Exp(-(centeredLn * centeredLn) / MidGaussianDivisor);
        l += midMask * midtonesDelta;

        var highlightsDelta = (highlights - 1f) * HighlightsStrength;
        var hlMask = GpuHelper.SmoothStep(HighlightsFrom, HighlightsTo, ln);
        l -= hlMask * highlightsDelta;

        if (contrast != ContrastIdentity)
        {
            var x = (l - ContrastPivot) / ContrastScale;
            var sCurve = x * (1f - XMath.Abs(x));
            l += sCurve * (contrast - 1f) * ContrastStrength;
        }

        var chroma = XMath.Sqrt(a * a + b * b);
        var grayMask = 1f - GpuHelper.SmoothStep(GrayMaskStart, GrayMaskEnd, chroma);
        var vibranceScale = 1f + grayMask * (vibrance - 1f);
        a *= vibranceScale;
        b *= vibranceScale;

        l = XMath.Clamp(l, 0f, WhitePointL);
        lab[idx] = new Lab(l, a, b);
    }

    private static void LabToRgbKernel(Index1D idx, ArrayView1D<Lab, Stride1D.Dense> lab, ArrayView1D<Rgb, Stride1D.Dense> rgb)
    {
        var value = lab[idx];

        var fy = (value.L + LabOffset) / LabDivisor;
        var fx = value.A / 500f + fy;
        var fz = fy - value.B / 200f;

        static float InverseLabCurve(float t)
        {
            var t3 = t * t * t;
            return t3 > LabPivot ? t3 : (t - LabOffset / LabDivisor) / LabInvFactor;
        }

        var xr = InverseLabCurve(fx);
        var yr = InverseLabCurve(fy);
        var zr = InverseLabCurve(fz);

        var x = xr * LabToXScale;
        var y = yr * LabToYScale;
        var z = zr * LabToZScale;

        var rl = 3.2404542f * x - 1.5371385f * y - 0.4985314f * z;
        var gl = -0.9692660f * x + 1.8760108f * y + 0.0415560f * z;
        var bl = 0.0556434f * x - 0.2040259f * y + 1.0572252f * z;

        static float ToSrgb01(float c)
        {
            return c <= SrgbLinearThreshold ? SrgbLinearFactor * c : SrgbGammaScale * GpuHelper.Pow(c, 1f / SrgbGamma) - SrgbGammaOffset;
        }

        rgb[idx] = new Rgb(ToSrgb01(rl), ToSrgb01(gl), ToSrgb01(bl));
    }

    #endregion
}
