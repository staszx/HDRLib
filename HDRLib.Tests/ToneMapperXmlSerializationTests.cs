// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Tests;

using System.IO;
using HDRLib.ToneMapping;
using HDRLib.ToneMapping.Settings;
using NUnit.Framework;

public class ToneMapperXmlSerializationTests
{
    [TestCaseSource(nameof(AllSettings))]
    public void Load_FromXmlWithoutKnownType_RestoresMapperType(ToneMapperSettings settings, Type expectedMapperType)
    {
        var original = (ToneMapper)HDRLib.ToneMapping.Factories.ToneMapperFactory.Create(settings);

        using var stream = new MemoryStream();
        original.Save(stream);

        stream.Position = 0;
        var loaded = ToneMapper.Load(stream);

        Assert.That(loaded.GetType(), Is.EqualTo(expectedMapperType));
    }

    [TestCaseSource(nameof(AllSettings))]
    public void SettingsLoad_FromXmlWithoutKnownType_RestoresSettingsType(ToneMapperSettings settings, Type _)
    {
        using var stream = new MemoryStream();
        settings.Save(stream);

        stream.Position = 0;
        var loaded = ToneMapperSettings.Load(stream);

        Assert.That(loaded.GetType(), Is.EqualTo(settings.GetType()));
    }

    [Test]
    public void Save_UsesLegacyRootName()
    {
        var settings = new NaturalToneMapperSettings();

        var xml = settings.ToXml();

        Assert.That(xml, Does.Contain("<ToneMapperSerializationModel"));
    }

    [Test]
    public void SettingsLoad_WithSaturationFilterComplexes_RestoresFilters()
    {
        var settings = new NaturalToneMapperSettings
        {
            SaturationFilters = [SaturationFilterPresets.CreateSkinFilter(true)],
            SkinFilter = SaturationFilterPresets.CreateSkinFilter(true),
            GrayColorFilter = SaturationFilterPresets.CreateGrayFilter(true)
        };

        var loaded = (NaturalToneMapperSettings)ToneMapperSettings.LoadFromXml(settings.ToXml());

        Assert.Multiple(() =>
        {
            Assert.That(loaded.SaturationFilters, Has.Length.EqualTo(1));
            Assert.That(loaded.SaturationFilters[0].Enabled, Is.True);
            Assert.That(loaded.SaturationFilters[0].SaturationAdjustment, Is.EqualTo(settings.SaturationFilters[0].SaturationAdjustment));
            Assert.That(loaded.SkinFilter.Enabled, Is.True);
            Assert.That(loaded.SkinFilter.Ranges, Has.Length.GreaterThanOrEqualTo(1));
            Assert.That(loaded.GrayColorFilter.Enabled, Is.True);
            Assert.That(loaded.GrayColorFilter.Ranges, Has.Length.EqualTo(1));
        });
    }

    [Test]
    public void GetSaturationColorRanges_UsesOnlyEnabledFilterComplexes()
    {
        var settings = new NaturalToneMapperSettings
        {
            SkinFilter = SaturationFilterPresets.CreateSkinFilter(true),
            GrayColorFilter = SaturationFilterPresets.CreateGrayFilter(false)
        };

        var ranges = settings.GetSaturationColorRanges();

        Assert.That(ranges, Has.Length.EqualTo(settings.SkinFilter.Ranges.Length));
    }

    [Test]
    public void GetSaturationColorRanges_AppliesFilterLevelAdjustmentToAllRanges()
    {
        var settings = new NaturalToneMapperSettings
        {
            SaturationFilters =
            [
                new SaturationColorFilter
                {
                    Enabled = true,
                    SaturationAdjustment = 24f,
                    Ranges =
                    [
                        new SaturationColorRange { HueMin = 0f, HueMax = 20f, SaturationMin = 0f, SaturationMax = 1f, ValueMin = 0f, ValueMax = 1f, SaturationMultiplier = -10f },
                        new SaturationColorRange { HueMin = 40f, HueMax = 60f, SaturationMin = 0f, SaturationMax = 1f, ValueMin = 0f, ValueMax = 1f, SaturationMultiplier = -20f }
                    ]
                }
            ]
        };

        var ranges = settings.GetSaturationColorRanges();

        Assert.That(ranges.Select(x => x.SaturationMultiplier), Is.All.EqualTo(24f));
    }

    [Test]
    public void GetSaturationColorRanges_DropsZeroStrengthRanges()
    {
        var settings = new NaturalToneMapperSettings
        {
            SaturationFilters =
            [
                new SaturationColorFilter
                {
                    Enabled = true,
                    Ranges =
                    [
                        new SaturationColorRange { HueMin = 0f, HueMax = 20f, SaturationMin = 0f, SaturationMax = 1f, ValueMin = 0f, ValueMax = 1f, SaturationMultiplier = 0f },
                        new SaturationColorRange { HueMin = 40f, HueMax = 60f, SaturationMin = 0f, SaturationMax = 1f, ValueMin = 0f, ValueMax = 1f, SaturationMultiplier = -20f }
                    ]
                }
            ]
        };

        var ranges = settings.GetSaturationColorRanges();

        Assert.That(ranges, Has.Length.EqualTo(1));
        Assert.That(ranges[0].SaturationMultiplier, Is.EqualTo(-20f));
    }

    [TestCase("skin_filter.xml")]
    [TestCase("gray_filter.xml")]
    public void PresetFiles_LoadAsToneMapperSettings(string fileName)
    {
        var presetPath = FindRepoFile(Path.Combine("HDRLib.Tests", "Presets", "ToneMapperFilters", fileName));

        var settings = ToneMapperSettings.Load(presetPath);

        Assert.That(settings, Is.TypeOf<NaturalToneMapperSettings>());
    }

    [Test]
    public void SaturationFilterPresetStore_LoadDirectory_LoadsFilterFiles()
    {
        var presetDirectory = Path.GetDirectoryName(FindRepoFile(Path.Combine("HDRLib.Tests", "Presets", "SaturationFilters", "skin.xml")));

        var filters = SaturationFilterPresetStore.LoadDirectory(presetDirectory);

        Assert.Multiple(() =>
        {
            Assert.That(filters, Has.Count.GreaterThanOrEqualTo(5));
            Assert.That(filters.Any(x => x.Name == "Skin"), Is.True);
            Assert.That(filters.Any(x => x.Name == "Low gray tones"), Is.True);
            Assert.That(filters.Any(x => x.Name == "Christmas warm"), Is.True);
        });
    }

    private static IEnumerable<TestCaseData> AllSettings()
    {
        yield return new TestCaseData(new AcesFilmicTonemapperSettings(), typeof(AcesFilmicToneMapper));
        yield return new TestCaseData(new NaturalToneMapperSettings(), typeof(NaturalToneMapper));
        yield return new TestCaseData(new ContrastBalancerToneMapperSettings(), typeof(ContrastBalancerToneMapper));
        yield return new TestCaseData(new BrightnessBalancerToneMapperSettings(), typeof(BrightnessBalancerToneMapper));
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(path))
            {
                return path;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Cannot find repository file '{relativePath}'.");
    }
}
