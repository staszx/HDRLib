// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping;

using Image;

internal static class DehazeProcessor
{
    private const float Epsilon = 1e-6f;

    public static void ApplyInPlace(Image<Rgb> image, float amount)
    {
        ApplyInPlace(image.Pixels, amount);
    }

    public static void ApplyInPlace(Rgb[] pixels, float amount)
    {
        var strength = Math.Clamp(amount / 100f, 0f, 1f);
        if (strength <= Epsilon)
        {
            return;
        }

        Parallel.For(0, pixels.Length, i =>
        {
            pixels[i] = Apply(pixels[i], strength);
        });
    }

    public static Rgb Apply(Rgb pixel, float strength)
    {
        var darkChannel = Math.Min(pixel.Red, Math.Min(pixel.Green, pixel.Blue));
        var veil = darkChannel * 0.95f * strength;
        var transmission = Math.Clamp(1f - veil, 0.35f, 1f);

        var recovered = new Rgb(
            Math.Clamp((pixel.Red - veil) / transmission, 0f, 1f),
            Math.Clamp((pixel.Green - veil) / transmission, 0f, 1f),
            Math.Clamp((pixel.Blue - veil) / transmission, 0f, 1f));

        var blend = strength * ToneMapperUtilities.SmoothStep(0.02f, 0.35f, darkChannel);
        return new Rgb(
            ToneMapperUtilities.Lerp(pixel.Red, recovered.Red, blend),
            ToneMapperUtilities.Lerp(pixel.Green, recovered.Green, blend),
            ToneMapperUtilities.Lerp(pixel.Blue, recovered.Blue, blend));
    }
}
