namespace HDRLib.Tests;

using HDRLib.Gpu;
using HDRLib.Image;
using HDRLib.ToneMapping.Factories;
using HDRLib.ToneMapping.Settings;
using ILGPU.Runtime;
using NUnit.Framework;

public class BrightnessWhiteClipRegressionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void WhiteClip_IsContinuousAndMonotonicAcrossNeutral(bool useGpu)
    {
        using var gpu = useGpu ? new GpuContext() : null;
        float[]? previous = null;
        foreach (var clip in new[] { 1.0001f, 1f, 0.9999f, 0.905f, 0.7625f, 0.62f, 0.05f })
        {
            var settings = new BrightnessBalancerToneMapperSettings().MakeNeutral();
            settings.Strength = 1;
            settings.WhiteClip = clip;
            var image = new Image<Rgb>(16, 1)
            {
                Width = 16, Height = 1,
                Pixels = Enumerable.Range(1, 16).Select(i => new Rgb(i / 20f, i / 20f, i / 20f)).ToArray()
            };
            if (gpu is null)
            {
                ToneMapperFactory.Create(settings).ApplyInPlace(image);
            }
            else
            {
                using var buffer = gpu.Accelerator.Allocate1D(image.Pixels);
                ToneMapperFactoryGpu.Create(gpu, settings).ApplyInPlace(buffer.View, image.Width, image.Height);
                image.Pixels = buffer.GetAsArray1D();
            }

            var current = image.Pixels.Select(pixel => pixel.Red).ToArray();
            for (var i = 0; i < current.Length; i++)
            {
                Assert.That(current[i], Is.EqualTo(Math.Min(1f, (i + 1) / 20f / clip)).Within(0.0001f));
                if (previous is not null)
                {
                    Assert.That(current[i], Is.GreaterThanOrEqualTo(previous[i] - 0.00001f));
                    if (clip >= 0.9999f)
                    {
                        Assert.That(Math.Abs(current[i] - previous[i]), Is.LessThan(0.0002f));
                    }
                }
            }

            previous = current;
        }
    }
}
