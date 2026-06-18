// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Tests;

using HDRLib.Image;
using HDRLib.ToneMapping;
using NUnit.Framework;

public class WhiteBalancerTests
{
    [Test]
    public void ApplyInPlace_WithoutParameters_UsesAutoMode()
    {
        var balancer = new WhiteBalancer();
        var image = new Image<Rgb>(1, 1)
        {
            Pixels = [new Rgb(0.8f, 0.4f, 0.2f)]
        };

        balancer.ApplyInPlace(image);

        Assert.Multiple(() =>
        {
            Assert.That(image.Pixels[0].Red, Is.EqualTo(0.64f).Within(1e-5f));
            Assert.That(image.Pixels[0].Green, Is.EqualTo(0.46666667f).Within(1e-5f));
            Assert.That(image.Pixels[0].Blue, Is.EqualTo(0.24f).Within(1e-5f));
        });
    }

    [Test]
    public void ApplyInPlace_WithBlackReference_BalancesTowardBlackPoint()
    {
        var balancer = new WhiteBalancer();
        var image = new Image<Rgb>(1, 1)
        {
            Pixels = [new Rgb(0.3f, 0.4f, 0.5f)]
        };

        balancer.ApplyInPlace(
            image,
            WhiteBalanceReferenceType.Black,
            new Rgb(0.02f, 0.04f, 0.01f));

        Assert.Multiple(() =>
        {
            Assert.That(image.Pixels[0].Red, Is.EqualTo(0.3f).Within(1e-5f));
            Assert.That(image.Pixels[0].Green, Is.EqualTo(0.2f).Within(1e-5f));
            Assert.That(image.Pixels[0].Blue, Is.EqualTo(1f).Within(1e-5f));
        });
    }

    [Test]
    public void ApplyInPlace_WithGrayReference_UsesProvidedNeutralSample()
    {
        var balancer = new WhiteBalancer();
        var image = new Image<Rgb>(1, 1)
        {
            Pixels = [new Rgb(0.25f, 0.5f, 0.75f)]
        };

        balancer.ApplyInPlace(
            image,
            WhiteBalanceReferenceType.Gray,
            new Rgb(0.25f, 0.5f, 1f));

        Assert.Multiple(() =>
        {
            Assert.That(image.Pixels[0].Red, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(image.Pixels[0].Green, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(image.Pixels[0].Blue, Is.EqualTo(0.375f).Within(1e-5f));
        });
    }
}
