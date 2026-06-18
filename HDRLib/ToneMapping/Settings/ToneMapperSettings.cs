// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping.Settings;

using HDRLib.Image;
using HDRLib.ToneMapping;
using System.Xml.Serialization;

/// <summary>
/// Base class for all tone‑mapper configuration settings.
/// </summary>
public abstract class ToneMapperSettings
{
    private static readonly Type[] KnownTypes =
    [
        typeof(AcesFilmicTonemapperSettings),
        typeof(AutoAdjustTonemapperSettings),
        typeof(NaturalToneMapperSettings),
        typeof(ContrastBalancerToneMapperSettings),
        typeof(BrightnessBalancerToneMapperSettings)
    ];

    private static readonly XmlSerializer Serializer = new(typeof(ToneMapperSettingsSerializationModel), KnownTypes);

    /// <summary>
/// Gets or sets the automatic adjustment mode.
/// </summary>
public AutoAdjustType AutoAdjustType { get; set; } = AutoAdjustType.None;
    /// <summary>
/// Gets or sets the exposure value (EV).
/// </summary>
public float ExposureEV { get; set; } = 0.0f;
    /// <summary>
/// Gets or sets the brightness multiplier.
/// </summary>
public float Brightness { get; set; } = 1.0f;
    /// <summary>
/// Gets or sets the contrast multiplier.
/// </summary>
public float Contrast { get; set; } = 1.0f;
    /// <summary>
/// Gets or sets the shadows boost multiplier.
/// </summary>
public float ShadowsBoost { get; set; } = 1.0f;
    /// <summary>
/// Gets or sets the midtones boost multiplier.
/// </summary>
public float MidtonesBoost { get; set; } = 1.0f;
    /// <summary>
/// Gets or sets the highlights boost multiplier.
/// </summary>
public float HighlightsBoost { get; set; } = 1.0f;
    /// <summary>
/// Gets or sets the dehaze amount.
/// </summary>
public float Dehaze { get; set; } = 0.0f;
    /// <summary>
/// Gets or sets the local contrast amount.
/// </summary>
public float LocalContrast { get; set; } = 0.0f;
    /// <summary>
/// Gets or sets the radius used for local contrast calculations.
/// </summary>
public int LocalContrastRadius { get; set; } = 1;
    /// <summary>
/// Gets or sets the transparency factor.
/// </summary>
public float Transparent { get; set; } = 0.0f;
    /// <summary>
/// Gets or sets the overall saturation level.
/// </summary>
public float Saturation { get; set; } = 0.0f;
    /// <summary>
/// Gets or sets the color ranges to exclude from saturation adjustments.
/// </summary>
public SaturationColorRange[] SaturateExcludes { get; set; } = [];
    /// <summary>
/// Gets or sets the directory containing saturation filter presets.
/// </summary>
public string? SaturationFilterPresetsDirectory { get; set; }
    /// <summary>
/// Gets or sets the collection of saturation color filters.
/// </summary>
public SaturationColorFilter[] SaturationFilters { get; set; } = [];
    /// <summary>
/// Gets or sets the skin tone saturation filter.
/// </summary>
public SaturationColorFilter SkinFilter { get; set; } = SaturationFilterPresets.CreateSkinFilter();
    /// <summary>
/// Gets or sets the gray tone saturation filter.
/// </summary>
public SaturationColorFilter GrayColorFilter { get; set; } = SaturationFilterPresets.CreateGrayFilter();
    /// <summary>
/// Gets or sets the gamma correction value.
/// </summary>
public float Gamma { get; set; } = 1.5f;
    /// <summary>
/// Gets or sets the color temperature in Kelvin.
/// </summary>
public float ColorTemperature { get; set; } = 6500.0f;
    /// <summary>
/// Gets or sets the white‑balance reference type.
/// </summary>
public WhiteBalanceReferenceType WhiteBalanceReferenceType { get; set; } = WhiteBalanceReferenceType.None;
    /// <summary>
/// Gets or sets the reference color for white‑balance calculations.
/// </summary>
public Rgb WhiteBalanceReferenceColor { get; set; } = new(1f, 1f, 1f);

    /// <summary>
/// Serialises the settings to the provided stream as XML.
/// </summary>
/// <param name="stream">Destination stream.</param>
public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var model = new ToneMapperSettingsSerializationModel
        {
            Settings = this
        };

        Serializer.Serialize(stream, model);
    }

    /// <summary>
/// Serialises the settings to a file at the specified path.
/// </summary>
/// <param name="path">File path to write the XML.</param>
public void Save(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var file = File.Create(path);
        this.Save(file);
    }

    /// <summary>
/// Returns the XML representation of these settings.
/// </summary>
/// <returns>XML string.</returns>
public string ToXml()
    {
        using var stream = new MemoryStream();
        this.Save(stream);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
/// Creates a deep copy of these settings.
/// </summary>
/// <returns>Cloned settings instance.</returns>
public ToneMapperSettings Clone()
    {
        return LoadFromXml(this.ToXml());
    }

    /// <summary>
/// Deserialises settings from a stream containing XML.
/// </summary>
/// <param name="stream">Source stream.</param>
/// <returns>Deserialized settings.</returns>
public static ToneMapperSettings Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var model = (ToneMapperSettingsSerializationModel?)Serializer.Deserialize(stream)
                    ?? throw new InvalidOperationException("Cannot deserialize tone mapper settings.");

        return model.Settings ?? throw new InvalidOperationException("Tone mapper settings are missing.");
    }

    /// <summary>
/// Deserialises settings from an XML file.
/// </summary>
/// <param name="path">File path.</param>
/// <returns>Deserialized settings.</returns>
public static ToneMapperSettings Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var file = File.OpenRead(path);
        return Load(file);
    }

    /// <summary>
/// Deserialises settings from an XML string.
/// </summary>
/// <param name="xml">XML content.</param>
/// <returns>Deserialized settings.</returns>
public static ToneMapperSettings LoadFromXml(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        return Load(stream);
    }

    [XmlRoot("ToneMapperSerializationModel")]
    /// <summary>
/// Helper class for XML serialisation of <see cref="ToneMapperSettings"/>.
/// </summary>
public sealed class ToneMapperSettingsSerializationModel
    {
        public ToneMapperSettings? Settings { get; set; }
    }
}
