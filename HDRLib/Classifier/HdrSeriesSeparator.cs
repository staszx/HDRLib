// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Classifier;

using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using ImageSharpImage = SixLabors.ImageSharp.Image;

public class HdrSeriesSeparator
{
    public HdrSeriesSeparator()
    {
    }

    public static SeparateImageResult SeparateHdrSeries(string inputFolder, IEnumerable<string> allowedExtensions)
    {
        var files = System.IO.Directory.GetFiles(inputFolder, "*.*")
            .Where(f => allowedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f)
            .ToList();

        var grouped = new Dictionary<int, List<(string path, double bias)>>();
        var result = new SeparateImageResult();
        var key = 0;

        foreach (var file in files)
        {
            try
            {
                var directories = ImageMetadataReader.ReadMetadata(file);
                var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();

                if (subIfd == null || !subIfd.TryGetRational(ExifDirectoryBase.TagExposureBias, out var exposureBias))
                {
                    result.SingleImages.Add(file);
                    continue;
                }

                var bias = exposureBias.ToDouble();
                key += bias == 0 ? 1 : 0;

                if (!grouped.ContainsKey(key))
                {
                    grouped[key] = new List<(string path, double bias)>();
                }

                grouped[key].Add((file, bias));
            }
            catch
            {
                Console.WriteLine($"Failed to read EXIF: {file}");
                result.SingleImages.Add(file);
            }
        }

        foreach (var group in grouped.Values)
        {
            foreach (var sizeGroup in SplitByImageSize(group, result.SingleImages))
            {
                var distinctBiases = sizeGroup.Select(g => g.bias).Distinct().ToList();

                if (sizeGroup.Count >= 2 && distinctBiases.Count >= 2)
                {
                    var hdrSeries = new List<string>();
                    foreach (var (file, _) in sizeGroup)
                    {
                        hdrSeries.Add(file);
                    }

                    result.HdrSeries.Add(hdrSeries);
                }
                else
                {
                    result.SingleImages.AddRange(sizeGroup.Select(g => g.path));
                }
            }
        }

        MergeNamedNeutralSingles(result);
        return result;
    }

    private static void MergeNamedNeutralSingles(SeparateImageResult result)
    {
        for (var singleIndex = result.SingleImages.Count - 1; singleIndex >= 0; singleIndex--)
        {
            var singleFile = result.SingleImages[singleIndex];
            if (!TryGetExposureBias(singleFile, out var singleBias) || Math.Abs(singleBias) > 0.001)
            {
                continue;
            }

            var singleBaseName = Path.GetFileNameWithoutExtension(singleFile);
            var singleSize = ImageSharpImage.Identify(singleFile);
            if (singleSize is null)
            {
                continue;
            }

            for (var seriesIndex = 0; seriesIndex < result.HdrSeries.Count; seriesIndex++)
            {
                var series = result.HdrSeries[seriesIndex];
                if (!TryGetNamedSeriesBase(series, out var seriesBaseName) ||
                    !string.Equals(seriesBaseName, singleBaseName, StringComparison.OrdinalIgnoreCase) ||
                    !HasMatchingSize(series, singleSize.Width, singleSize.Height))
                {
                    continue;
                }

                series.Insert(0, singleFile);
                result.SingleImages.RemoveAt(singleIndex);
                break;
            }
        }
    }

    private static bool TryGetNamedSeriesBase(IEnumerable<string> files, out string baseName)
    {
        baseName = string.Empty;
        foreach (var file in files)
        {
            if (!TryGetHdrSuffixBase(Path.GetFileNameWithoutExtension(file), out var currentBaseName))
            {
                return false;
            }

            if (baseName.Length == 0)
            {
                baseName = currentBaseName;
            }
            else if (!string.Equals(baseName, currentBaseName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return baseName.Length > 0;
    }

    private static bool TryGetHdrSuffixBase(string name, out string baseName)
    {
        foreach (var suffix in new[] { "_over", "_under" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                baseName = name[..^suffix.Length];
                return baseName.Length > 0;
            }
        }

        baseName = string.Empty;
        return false;
    }

    private static bool HasMatchingSize(IEnumerable<string> files, int width, int height)
    {
        foreach (var file in files)
        {
            var info = ImageSharpImage.Identify(file);
            if (info is null || info.Width != width || info.Height != height)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetExposureBias(string file, out double bias)
    {
        bias = 0;
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(file);
            var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (subIfd is null || !subIfd.TryGetRational(ExifDirectoryBase.TagExposureBias, out var exposureBias))
            {
                return false;
            }

            bias = exposureBias.ToDouble();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<List<(string path, double bias)>> SplitByImageSize(
        IEnumerable<(string path, double bias)> group,
        List<string> singleImages)
    {
        var groupsBySize = new Dictionary<(int Width, int Height), List<(string path, double bias)>>();
        foreach (var item in group)
        {
            try
            {
                var info = ImageSharpImage.Identify(item.path);
                if (info is null)
                {
                    singleImages.Add(item.path);
                    continue;
                }

                var key = (info.Width, info.Height);
                if (!groupsBySize.TryGetValue(key, out var sizeGroup))
                {
                    sizeGroup = [];
                    groupsBySize.Add(key, sizeGroup);
                }

                sizeGroup.Add(item);
            }
            catch
            {
                singleImages.Add(item.path);
            }
        }

        return groupsBySize.Values;
    }
}
