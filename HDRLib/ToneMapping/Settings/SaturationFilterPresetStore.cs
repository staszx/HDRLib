// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping.Settings;

using System.Xml.Serialization;

public static class SaturationFilterPresetStore
{
    private static readonly XmlSerializer Serializer = new(typeof(SaturationColorFilter));

    public static IReadOnlyList<SaturationColorFilter> LoadDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        var filters = new List<SaturationColorFilter>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.xml").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var filter = Load(file);
            filter.Name ??= Path.GetFileNameWithoutExtension(file);
            filters.Add(filter);
        }

        return filters;
    }

    public static SaturationColorFilter Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var file = File.OpenRead(path);
        return (SaturationColorFilter?)Serializer.Deserialize(file)
               ?? throw new InvalidOperationException($"Cannot deserialize saturation filter preset: {path}");
    }

    public static void Save(string path, SaturationColorFilter filter)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(filter);

        using var file = File.Create(path);
        Serializer.Serialize(file, filter);
    }
}
