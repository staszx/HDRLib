// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using System.Runtime.CompilerServices;
using HDRLib.Adjust;
using HDRLib.Image;
using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;

/// <summary>
/// Common scalar and GPU helpers used by tone mappers.
/// </summary>
internal static class ToneMapperUtilities
{
    private const float ContrastIdentity = 1.0f;
    private const float ContrastIdentityEpsilon = 1e-6f;
    private const float GammaIdentityEpsilon = 1e-3f;

    /// <summary>
    /// Applies the fitted ACES curve and clamps negative output.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float AcesFitted(float x)
    {
        if (x < 0)
        {
            x = 0;
        }

        var num = (x * (x + AcesConstants.FitA)) - AcesConstants.FitB;
        var den = (x * ((AcesConstants.FitC * x) + AcesConstants.FitD)) + AcesConstants.FitE;

        return Math.Max(num / den, AcesConstants.ChannelMin);
    }

    /// <summary>
    /// Adjusts contrast around the ACES contrast pivot.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float AdjustContrast(float value, float contrast)
    {
        if (MathF.Abs(contrast - ContrastIdentity) < ContrastIdentityEpsilon)
        {
            return value;
        }

        return Math.Clamp(
            ((value - AcesConstants.ContrastPivot) * contrast) + AcesConstants.ContrastPivot,
            AcesConstants.ChannelMin,
            AcesConstants.ChannelMax);
    }

    /// <summary>
    /// Linearly interpolates from one value to another.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Lerp(float from, float to, float amount)
    {
        return from + ((to - from) * amount);
    }

    /// <summary>
    /// Performs Hermite interpolation between two edges.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = XMath.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    /// <summary>
    /// Computes automatic exposure from a log-average luminance.
    /// </summary>
    public static float ComputeAutoExposure(ReadOnlySpan<Rgb> pixels, float delta, float epsilon)
    {
        if (pixels.IsEmpty)
        {
            return 1f;
        }

        var logSum = 0.0f;
        for (var i = 0; i < pixels.Length; i++)
        {
            logSum += MathF.Log(delta + pixels[i].Light());
        }

        var avgLuminance = MathF.Exp(logSum / pixels.Length);
        return 0.18f / (avgLuminance + epsilon);
    }

    /// <summary>
    /// Applies inverse gamma correction to pixels in place.
    /// </summary>
    public static void ApplyGamma(Span<Rgb> pixels, float gamma)
    {
        if (MathF.Abs(gamma - 1f) <= GammaIdentityEpsilon)
        {
            return;
        }

        var invGamma = 1.0f / MathF.Max(gamma, 0.1f);
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            pixels[i] = new Rgb(
                Math.Clamp(MathF.Pow(p.Red, invGamma), AcesConstants.ChannelMin, AcesConstants.ChannelMax),
                Math.Clamp(MathF.Pow(p.Green, invGamma), AcesConstants.ChannelMin, AcesConstants.ChannelMax),
                Math.Clamp(MathF.Pow(p.Blue, invGamma), AcesConstants.ChannelMin, AcesConstants.ChannelMax));
        }
    }

    /// <summary>
    /// Computes automatic exposure on the GPU; reading the result synchronizes the reduction.
    /// </summary>
    internal static float ComputeAutoExposure(
        Accelerator accelerator,
        ArrayView1D<Rgb, Stride1D.Dense> pixels,
        Action<Index1D, ArrayView1D<Rgb, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, float> exposureLogSumKernel,
        float key,
        float delta,
        float epsilon)
    {
        if (pixels.Length == 0)
        {
            return 1f;
        }

        using var logSumBuffer = accelerator.Allocate1D<float>(1);
        logSumBuffer.MemSetToZero();
        exposureLogSumKernel((int)pixels.Length, pixels, logSumBuffer.View, delta);

        var logSum = logSumBuffer.GetAsArray1D()[0];
        var avgLuminance = GpuHelper.Exp(logSum / pixels.Length);
        return key / (avgLuminance + epsilon);
    }

    internal static void ExposureLogSumKernel(
        Index1D index,
        ArrayView1D<Rgb, Stride1D.Dense> pixels,
        ArrayView1D<float, Stride1D.Dense> logSum,
        float delta)
    {
        Atomic.Add(ref logSum[0], GpuHelper.Log(delta + pixels[index].Light()));
    }
}
