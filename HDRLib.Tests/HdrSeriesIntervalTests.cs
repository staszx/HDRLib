namespace HDRLib.Tests;

using System.Globalization;
using HDRLib.Classifier;
using NUnit.Framework;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

public class HdrSeriesIntervalTests
{
    private string directory = null!;

    [SetUp]
    public void SetUp()
    {
        this.directory = Path.Combine(Path.GetTempPath(), "hdr-interval-" + Guid.NewGuid());
        Directory.CreateDirectory(this.directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(this.directory, true);

    [TestCase(1d, 1, 0)]
    [TestCase(1.001d, 0, 2)]
    public void DefaultInterval_IncludesBoundaryAndRejectsLargerGap(double gap, int series, int singles)
    {
        this.WriteFrame("a", 0, 0);
        this.WriteFrame("b", -1, gap);
        var result = HdrSeriesSeparator.SeparateHdrSeries(this.directory, [".jpg"]);
        Assert.That(result.HdrSeries, Has.Count.EqualTo(series));
        Assert.That(result.SingleImages, Has.Count.EqualTo(singles));
    }

    [Test]
    public void CustomInterval_IsBetweenAdjacentFramesNotTotalDuration()
    {
        this.WriteFrame("a", 0, 0);
        this.WriteFrame("b", -1, 2);
        this.WriteFrame("c", 1, 4);
        var result = HdrSeriesSeparator.SeparateHdrSeries(this.directory, [".jpg"], TimeSpan.FromSeconds(2));
        Assert.That(result.HdrSeries, Has.Count.EqualTo(1));
        Assert.That(result.HdrSeries[0], Has.Count.EqualTo(3));
        Assert.That(result.SingleImages, Is.Empty);
    }

    [Test]
    public void Gap_SplitsIntoValidGroupsWithoutDroppingFrames()
    {
        this.WriteFrame("a", 0, 0);
        this.WriteFrame("b", -1, 1);
        this.WriteFrame("c", 1, 5);
        this.WriteFrame("d", -1, 6);
        var result = HdrSeriesSeparator.SeparateHdrSeries(this.directory, [".jpg"]);
        Assert.That(result.HdrSeries, Has.Count.EqualTo(2));
        Assert.That(result.HdrSeries.SelectMany(x => x).Distinct().Count(), Is.EqualTo(4));
        Assert.That(result.SingleImages, Is.Empty);
    }

    [Test]
    public void NamedNeutralMerge_CannotBypassInterval()
    {
        this.WriteFrame("scene", 0, 0);
        this.WriteFrame("scene_over", 1, 5);
        this.WriteFrame("scene_under", -1, 6);
        var result = HdrSeriesSeparator.SeparateHdrSeries(this.directory, [".jpg"]);
        Assert.That(result.HdrSeries, Has.Count.EqualTo(1));
        Assert.That(result.HdrSeries[0], Has.Count.EqualTo(2));
        Assert.That(result.SingleImages.Select(Path.GetFileNameWithoutExtension), Is.EquivalentTo(new[] { "scene" }));
    }

    [Test]
    public void MissingCaptureTime_RemainsSingle()
    {
        this.WriteFrame("a", 0, null);
        this.WriteFrame("b", -1, 0);
        var result = HdrSeriesSeparator.SeparateHdrSeries(this.directory, [".jpg"]);
        Assert.That(result.HdrSeries, Is.Empty);
        Assert.That(result.SingleImages, Has.Count.EqualTo(2));
    }

    [Test]
    public void SplitGroup_WithSameExposureBias_RemainsSingle()
    {
        this.WriteFrame("a", 0, 0);
        this.WriteFrame("b", -1, 5);
        this.WriteFrame("c", -1, 6);
        var result = HdrSeriesSeparator.SeparateHdrSeries(this.directory, [".jpg"]);
        Assert.That(result.HdrSeries, Is.Empty);
        Assert.That(result.SingleImages, Has.Count.EqualTo(3));
    }

    private void WriteFrame(string name, int bias, double? seconds)
    {
        using var image = new Image<Rgb24>(8, 8);
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.ExposureBiasValue, new SignedRational(bias, 1));
        if (seconds.HasValue)
        {
            var time = new DateTime(2026, 10, 5, 12, 0, 0).AddSeconds(seconds.Value);
            image.Metadata.ExifProfile.SetValue(ExifTag.DateTimeOriginal, time.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture));
            image.Metadata.ExifProfile.SetValue(ExifTag.SubsecTimeOriginal, time.ToString("fffffff", CultureInfo.InvariantCulture));
        }

        image.SaveAsJpeg(Path.Combine(this.directory, name + ".jpg"));
    }
}
