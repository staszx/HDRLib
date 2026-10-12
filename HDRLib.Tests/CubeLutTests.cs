namespace HDRLib.Tests;

using System.Globalization;
using System.Text;
using HDRLib.ColorGrading;
using HDRLib.Gpu;
using HDRLib.Hdr.Debevec;
using HDRLib.Image;
using HDRLib.Interfaces;
using HDRLib.PixelProvider.ImageSharp;
using HDRLib.ToneMapping.Settings;
using NUnit.Framework;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

public class CubeLutTests
{
    private static CubeLut Parse(string text) => CubeLut.Parse(new StringReader(text));

    private static string Make3D(Func<float, float, float, Rgb> map, int size = 2)
    {
        var text = new StringBuilder($"LUT_3D_SIZE {size}\n");
        for (var b = 0; b < size; b++)
        for (var g = 0; g < size; g++)
        for (var r = 0; r < size; r++)
        {
            var value = map(r / (float)(size - 1), g / (float)(size - 1), b / (float)(size - 1));
            text.AppendLine(FormattableString.Invariant($"{value.Red:R} {value.Green:R} {value.Blue:R}"));
        }

        return text.ToString();
    }

    [TestCase(2)]
    [TestCase(3)]
    [TestCase(17)]
    public void Identity_PreservesInteriorEdgesAndCorners(int size)
    {
        var lut = Parse(Make3D((r, g, b) => new Rgb(r, g, b), size));
        var random = new Random(19);
        for (var i = 0; i < 128; i++)
        {
            var input = new Rgb(random.NextSingle(), random.NextSingle(), random.NextSingle());
            AssertRgb(lut.Transform(input), input);
        }

        AssertRgb(lut.Transform(new Rgb(0, 0, 0)), new Rgb(0, 0, 0));
        AssertRgb(lut.Transform(new Rgb(1, 1, 1)), new Rgb(1, 1, 1));
        AssertRgb(lut.Transform(new Rgb(-5, 4, 0.2f)), new Rgb(0, 1, 0.2f));
    }

    [Test]
    public void ThreeDimensional_UsesRedFastestAndTrilinearInterpolation()
    {
        var lut = Parse(Make3D((r, g, b) => new Rgb(b, r * g, 1 - r)));
        AssertRgb(lut.Transform(new Rgb(0.2f, 0.7f, 0.4f)), new Rgb(0.4f, 0.14f, 0.8f));
    }

    [Test]
    public void DomainAndTitle_AllowWhitespaceCommentsAndScientificNotation()
    {
        var lut = Parse("# comment\nTITLE \"Look # 1\" # comment\nDOMAIN_MIN -1 -2 -3\nDOMAIN_MAX 1 2 3\n" +
                        "LUT_1D_SIZE\t2\n-2e-1\t0 0 # first\n1.2 1 1\n");
        Assert.That(lut.Title, Is.EqualTo("Look # 1"));
        Assert.That(lut.Is3D, Is.False);
        AssertRgb(lut.Transform(new Rgb(0, 0, 0)), new Rgb(0.5f, 0.5f, 0.5f));
        AssertRgb(lut.Transform(new Rgb(-1, -2, -3)), new Rgb(-0.2f, 0, 0));
    }

    [Test]
    public void OneDimensional_InterpolatesEachChannelIndependently()
    {
        var lut = Parse("LUT_1D_SIZE 3\n0 1 0\n0.25 0.5 0.8\n1 0 1\n");
        AssertRgb(lut.Transform(new Rgb(0.25f, 0.75f, 0.5f)), new Rgb(0.125f, 0.25f, 0.8f));
    }

    [Test]
    public void InputRange_IsRespected()
    {
        var lut = Parse("LUT_1D_SIZE 2\nLUT_1D_INPUT_RANGE -2 2\n0 0 0\n1 1 1\n");
        AssertRgb(lut.Transform(new Rgb(-1, 0, 1)), new Rgb(0.25f, 0.5f, 0.75f));
    }

    [TestCase(0f)]
    [TestCase(0.25f)]
    [TestCase(1f)]
    public void Strength_BlendsAfterLookupAndSupportsByteRangeFloats(float strength)
    {
        var lut = Parse("LUT_1D_SIZE 2\n1 1 1\n0 0 0\n");
        var pixels = new[] { new Rgb(51, 102, 204) };
        lut.ApplyInPlace(pixels, strength, 255f);
        AssertRgb(pixels[0] / 255f, new Rgb(0.2f + 0.6f * strength, 0.4f + 0.2f * strength, 0.8f - 0.6f * strength));
    }

    [TestCase("")]
    [TestCase("LUT_3D_SIZE 1\n0 0 0")]
    [TestCase("LUT_3D_SIZE 999999999\n0 0 0")]
    [TestCase("LUT_1D_SIZE 2\n0 0 0")]
    [TestCase("LUT_1D_SIZE 2\n0 0 0\n1 1 1\n2 2 2")]
    [TestCase("LUT_1D_SIZE 2\n0 0 0\n1 NaN 1")]
    [TestCase("LUT_1D_SIZE 2\n0 0 0\n1 1e100 1")]
    [TestCase("LUT_1D_SIZE 2\n0 0 0\n1 1")]
    [TestCase("LUT_1D_SIZE 2\nDOMAIN_MAX 0 1 1\n0 0 0\n1 1 1")]
    [TestCase("LUT_1D_SIZE 2\nDOMAIN_MIN 2 0 0\n0 0 0\n1 1 1")]
    [TestCase("LUT_1D_SIZE 2\nLUT_1D_SIZE 2\n0 0 0\n1 1 1")]
    [TestCase("LUT_1D_SIZE 2\nLUT_3D_SIZE 2\n0 0 0\n1 1 1")]
    [TestCase("LUT_1D_SIZE 2\nLUT_3D_INPUT_RANGE 0 1\n0 0 0\n1 1 1")]
    [TestCase("LUT_1D_SIZE 2\nDOMAIN_MIN 0 0 0\nLUT_1D_INPUT_RANGE 0 1\n0 0 0\n1 1 1")]
    [TestCase("LUT_1D_SIZE 2\n0 0 0\nDOMAIN_MIN 0 0 0\n1 1 1")]
    [TestCase("TITLE invalid\nLUT_1D_SIZE 2\n0 0 0\n1 1 1")]
    public void MalformedInput_IsRejected(string text) => Assert.Throws<FormatException>(() => Parse(text));

    [Test]
    public void Load_LeavesStreamOpenAndIgnoresCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            using var stream = new MemoryStream(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("LUT_1D_SIZE 2\n0.1 0 0\n0.9 1 1\n")).ToArray());
            var lut = CubeLut.Load(stream);
            Assert.That(stream.CanRead, Is.True);
            AssertRgb(lut.Transform(new Rgb(0.5f, 0.5f, 0.5f)), new Rgb(0.5f, 0.5f, 0.5f));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void SingleImage_LutRunsAfterToneMapperAndDoesNotAccumulate(bool useGpu, bool neutral)
    {
        using var source = CreateSource(1);
        using var context = useGpu ? new GpuContext() : null;
        using var processor = new SingleImageProcessor(context);
        processor.LoadSource(source);
        var settings = new BrightnessBalancerToneMapperSettings().MakeNeutral();
        if (!neutral) { settings.WhiteClip = 0.8f; settings.Strength = 1; }
        var lut = Parse("LUT_1D_SIZE 2\n1 1 1\n0 0 0\n");
        processor.Process(settings);
        using var baseline = processor.ToImage<ImageSharpProxy>();
        for (var pass = 0; pass < 2; pass++)
        {
            processor.ProcessWithLut(settings, lut);
            using var actual = processor.ToImage<ImageSharpProxy>();
            for (var y = 0; y < actual.Height; y++)
            {
                var expected = baseline.LoadRow(y).Select(value => 255 - value).ToArray();
                var row = actual.LoadRow(y);
                for (var i = 0; i < row.Length; i++) Assert.That((int)row[i], Is.EqualTo(expected[i]).Within(1));
            }
        }

        processor.ProcessWithLut(settings, lut, 0);
        using var disabled = processor.ToImage<ImageSharpProxy>();
        for (var y = 0; y < disabled.Height; y++) Assert.That(disabled.LoadRow(y), Is.EqualTo(baseline.LoadRow(y)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void HdrProcessor_LutRunsAfterToneMappingAndEncoding(bool useGpu)
    {
        using var first = CreateSource(0.25);
        using var second = CreateSource(0.5);
        using var third = CreateSource(1);
        using var context = useGpu ? new GpuContext() : null;
        var processor = new HDRProcessor<ImageSharpProxy>(toneMapperSettings: null, context: context);
        var options = new HdrImageOptions
        {
            SampleCount = 32, MotionFilterStrength = 0,
            ToneMapperSettings = new BrightnessBalancerToneMapperSettings { WhiteClip = 0.8f }
        };
        var sources = new List<IImageProxy> { first, second, third };
        using var expected = processor.Process(sources, options);
        var lut = Parse(Make3D((r, g, b) => new Rgb(1 - b, 1 - r, 1 - g)));
        lut.ApplyInPlace(expected);
        options.OutputLut = lut;
        using var actual = processor.Process(sources, options);
        for (var y = 0; y < actual.Height; y++) Assert.That(actual.LoadRow(y), Is.EqualTo(expected.LoadRow(y)));
    }

    private static ImageSharpProxy CreateSource(double exposure)
    {
        using var image = new SixLabors.ImageSharp.Image<Rgb24>(16, 8);
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.ExposureTime, new Rational(exposure));
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
            image[x, y] = new Rgb24((byte)((40 + x * 10) * exposure), (byte)((50 + y * 20) * exposure), (byte)((30 + x * 8) * exposure));
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        stream.Position = 0;
        var proxy = new ImageSharpProxy();
        proxy.Load(stream);
        return proxy;
    }

    private static void AssertRgb(Rgb actual, Rgb expected)
    {
        Assert.That(actual.Red, Is.EqualTo(expected.Red).Within(1e-5));
        Assert.That(actual.Green, Is.EqualTo(expected.Green).Within(1e-5));
        Assert.That(actual.Blue, Is.EqualTo(expected.Blue).Within(1e-5));
    }
}
