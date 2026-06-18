// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Tests;

using HDRLib.ToneMapping;
using NUnit.Framework;

public class AcesConstantsTests
{
    [Test]
    public void Constants_MatchPreviousAcesValues()
    {
        var input = new[]
        {
            AcesConstants.Input00, AcesConstants.Input01, AcesConstants.Input02,
            AcesConstants.Input10, AcesConstants.Input11, AcesConstants.Input12,
            AcesConstants.Input20, AcesConstants.Input21, AcesConstants.Input22
        };
        var output = new[]
        {
            AcesConstants.Output00, AcesConstants.Output01, AcesConstants.Output02,
            AcesConstants.Output10, AcesConstants.Output11, AcesConstants.Output12,
            AcesConstants.Output20, AcesConstants.Output21, AcesConstants.Output22
        };

        Assert.That(input, Is.EqualTo(new[] { 0.59719f, 0.35458f, 0.04823f, 0.07600f, 0.90834f, 0.01566f, 0.02840f, 0.13383f, 0.83777f }));
        Assert.That(output, Is.EqualTo(new[] { 1.60475f, -0.53108f, -0.07367f, -0.10208f, 1.10813f, -0.00605f, -0.00327f, -0.07276f, 1.07602f }));
        Assert.That(AcesConstants.FitA, Is.EqualTo(0.0245786f));
        Assert.That(AcesConstants.FitB, Is.EqualTo(0.000090537f));
        Assert.That(AcesConstants.FitC, Is.EqualTo(0.983729f));
        Assert.That(AcesConstants.FitD, Is.EqualTo(0.4329510f));
        Assert.That(AcesConstants.FitE, Is.EqualTo(0.238081f));
        Assert.That(AcesConstants.ExposureDelta, Is.EqualTo(1e-4f));
        Assert.That(AcesConstants.ExposureEpsilon, Is.EqualTo(1e-9f));
        Assert.That(AcesConstants.ChannelMin, Is.EqualTo(0f));
        Assert.That(AcesConstants.ChannelMax, Is.EqualTo(1f));
        Assert.That(AcesConstants.ContrastPivot, Is.EqualTo(0.5f));
    }
}
