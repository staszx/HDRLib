// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Hdr.Debevec;

/// <summary>Returns merged radiance to camera code values at the reference exposure.</summary>
internal sealed class CameraResponseEncoder
{
    private readonly double[][] response;
    private readonly double logExposure;

    public CameraResponseEncoder(double[][] response, double logExposure)
    {
        this.logExposure = logExposure;
        this.response = response.Select(channel => channel.ToArray()).ToArray();
        // A fitted curve can have small decreasing sections near clipping.
        // The inverse requires a nondecreasing response.
        foreach (var channel in this.response)
        {
            for (var i = 1; i < channel.Length; i++)
            {
                channel[i] = Math.Max(channel[i], channel[i - 1]);
            }
        }
    }

    public float Encode(float radiance, int channel)
    {
        var curve = this.response[channel];
        var value = Math.Log(radiance) + this.logExposure;
        if (radiance <= 0 || value <= curve[0]) return 0;
        if (value >= curve[255]) return 255;

        return InvertResponse(curve, value);
    }

    /// <summary>
    /// Encodes linear input normalized to the channel's camera white level.
    /// One represents the radiance corresponding to code 255 at the reference
    /// exposure; zero represents no light. Returns camera code values in [0, 1].
    /// Values outside [0, 1] are clipped. The input must not already be gamma encoded.
    /// A flat fitted response has no inverse; in that case the input is preserved.
    /// </summary>
    public float EncodeNormalized(float linearValue, int channel)
    {
        var curve = this.response[channel];
        if (float.IsNaN(linearValue)) throw new ArgumentOutOfRangeException(nameof(linearValue));
        if (linearValue <= 0) return 0;
        if (linearValue >= 1) return 1;

        // Missing exposure metadata can leave the solver with a constant response.
        // Its numerical noise carries no transfer function to apply.
        if (curve[255] - curve[0] <= 1e-6d) return linearValue;

        // E = linearValue * exp(g(255) - logExposure), so
        // log(E) + logExposure = log(linearValue) + g(255).
        var value = Math.Log(linearValue) + curve[255];
        if (value <= curve[0]) return 0;
        return InvertResponse(curve, value) / 255f;
    }

    private static float InvertResponse(double[] curve, double value)
    {
        var low = 0;
        var high = 255;
        while (high - low > 1)
        {
            var middle = (low + high) / 2;
            if (curve[middle] < value) low = middle;
            else high = middle;
        }

        var interval = curve[high] - curve[low];
        return interval > 0 ? (float)(low + (value - curve[low]) / interval) : low;
    }

    internal float[] CreateLookup(bool normalized)
    {
        // Rebase in double before conversion so arbitrary response offsets do
        // not destroy the precision of the float SIMD/GPU tables.
        var lookup = new float[3 * 256 + 3];
        for (var channel = 0; channel < 3; channel++)
        {
            var curve = this.response[channel];
            var offset = normalized ? curve[255] : this.logExposure;
            for (var z = 0; z < 256; z++) lookup[channel * 256 + z] = (float)(curve[z] - offset);
            lookup[768 + channel] = curve[255] - curve[0] <= 1e-6d ? 1f : 0f;
        }
        return lookup;
    }
}
