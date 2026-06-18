// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Tests;

using HDRLib.Image;
using HDRLib.ToneMapping;
using NUnit.Framework;

public class ToneMapperUtilitiesTests
{
    [Test]
    public void AcesFitted_ReturnsPositiveClampedValues()
    {
        Assert.That(ToneMapperUtilities.AcesFitted(-1f), Is.EqualTo(0f));
        Assert.That(ToneMapperUtilities.AcesFitted(0.18f), Is.GreaterThan(0f));
        Assert.That(ToneMapperUtilities.AcesFitted(1f), Is.GreaterThan(0f));
    }

    [Test]
    public void AdjustContrast_HandlesPivotAndIdentity()
    {
        Assert.That(ToneMapperUtilities.AdjustContrast(AcesConstants.ContrastPivot, 2f), Is.EqualTo(AcesConstants.ContrastPivot).Within(1e-6f));
        Assert.That(ToneMapperUtilities.AdjustContrast(0.25f, 1f), Is.EqualTo(0.25f).Within(1e-6f));
        Assert.That(ToneMapperUtilities.AdjustContrast(0.25f, 2f), Is.EqualTo(0f).Within(1e-6f));
    }

    [Test]
    public void ComputeAutoExposure_UsesLogAverageLuminance()
    {
        var pixels = new[]
        {
            new Rgb(0.25f, 0.25f, 0.25f),
            new Rgb(0.5f, 0.5f, 0.5f)
        };

        var expectedAverage = MathF.Exp((MathF.Log(AcesConstants.ExposureDelta + 0.25f) + MathF.Log(AcesConstants.ExposureDelta + 0.5f)) / 2f);
        var expected = 0.18f / (expectedAverage + AcesConstants.ExposureEpsilon);

        Assert.That(ToneMapperUtilities.ComputeAutoExposure(pixels, AcesConstants.ExposureDelta, AcesConstants.ExposureEpsilon), Is.EqualTo(expected).Within(1e-6f));
    }
}
