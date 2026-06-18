// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ToneMapping.Settings;

public static class ToneMapperSettingsSaturationFilters
{
    public static SaturationColorRange[] GetSaturationColorRanges(this ToneMapperSettings settings)
    {
        var ranges = new List<SaturationColorRange>();
        AddRanges(ranges, settings.SaturateExcludes);
        AddRanges(ranges, settings.SaturationFilters);
        AddRanges(ranges, settings.SkinFilter);
        AddRanges(ranges, settings.GrayColorFilter);
        return ranges.Count == 0 ? [] : ranges.ToArray();
    }

    private static void AddRanges(List<SaturationColorRange> target, SaturationColorRange[]? ranges)
    {
        if (ranges is { Length: > 0 })
        {
            target.AddRange(ranges);
        }
    }

    private static void AddRanges(List<SaturationColorRange> target, SaturationColorFilter? filter)
    {
        if (filter?.Enabled != true || filter.Ranges.Length == 0)
        {
            return;
        }

        if (MathF.Abs(filter.SaturationAdjustment) <= 1e-6f)
        {
            AddRanges(target, filter.Ranges);
            return;
        }

        foreach (var range in filter.Ranges)
        {
            target.Add(range with { SaturationMultiplier = filter.SaturationAdjustment });
        }
    }

    private static void AddRanges(List<SaturationColorRange> target, SaturationColorFilter[]? filters)
    {
        if (filters is null)
        {
            return;
        }

        foreach (var filter in filters)
        {
            AddRanges(target, filter);
        }
    }
}
