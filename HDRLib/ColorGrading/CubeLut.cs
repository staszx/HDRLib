// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.ColorGrading;

using System.Globalization;
using System.Text;
using HDRLib.Image;
using HDRLib.Interfaces;

/// <summary>An immutable Adobe/IRIDAS .cube table (1D or 3D), safe to share between images.</summary>
public sealed class CubeLut
{
    private readonly Rgb[] values;
    private readonly Rgb domainMin;
    private readonly Rgb domainMax;

    internal Rgb[] Values => this.values;
    internal Rgb DomainMin => this.domainMin;
    internal Rgb DomainMax => this.domainMax;

    private CubeLut(string? title, int size, bool is3D, Rgb min, Rgb max, Rgb[] values)
    {
        this.Title = title;
        this.Size = size;
        this.Is3D = is3D;
        this.domainMin = min;
        this.domainMax = max;
        this.values = values;
    }

    /// <summary>Optional title stored in the file.</summary>
    public string? Title { get; }
    /// <summary>Number of samples per axis.</summary>
    public int Size { get; }
    /// <summary>True for a 3D table; false for three independent 1D channel curves.</summary>
    public bool Is3D { get; }

    /// <summary>Loads a .cube file. Input and output values are RGB, not BGR.</summary>
    public static CubeLut Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    /// <summary>Loads a .cube stream, leaving the caller's stream open.</summary>
    public static CubeLut Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 16384, leaveOpen: true);
        return Parse(reader);
    }

    /// <summary>Parses a table using invariant numeric syntax. Leaves the reader open.</summary>
    public static CubeLut Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        string? title = null;
        var min = new Rgb(0, 0, 0);
        var max = new Rgb(1, 1, 1);
        var headers = new HashSet<string>(StringComparer.Ordinal);
        var size = 0;
        var is3D = false;
        Rgb[]? data = null;
        var count = 0;
        var lineNumber = 0;
        string? raw;
        while ((raw = reader.ReadLine()) is not null)
        {
            lineNumber++;
            var quoted = false;
            for (var i = 0; i < raw.Length; i++)
            {
                if (raw[i] == '"') quoted = !quoted;
                if (raw[i] == '#' && !quoted) { raw = raw[..i]; break; }
            }

            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var key = parts[0];
            if (char.IsLetter(key[0]))
            {
                if (count != 0) throw Error(lineNumber, "Headers must precede table values.");
                if (!headers.Add(key)) throw Error(lineNumber, $"Duplicate {key} header.");
                switch (key)
                {
                    case "TITLE":
                        var text = line[key.Length..].Trim();
                        if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
                            throw Error(lineNumber, "TITLE must be quoted.");
                        title = text[1..^1];
                        break;
                    case "LUT_1D_SIZE":
                    case "LUT_3D_SIZE":
                        if (size != 0) throw Error(lineNumber, "Combined 1D + 3D tables are not supported.");
                        is3D = key == "LUT_3D_SIZE";
                        if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out size) ||
                            size < 2 || size > (is3D ? 256 : 65536))
                            throw Error(lineNumber, "Invalid LUT size (3D: 2..256; 1D: 2..65536).");
                        break;
                    case "DOMAIN_MIN":
                        min = ReadRgb(parts, 1, lineNumber);
                        break;
                    case "DOMAIN_MAX":
                        max = ReadRgb(parts, 1, lineNumber);
                        break;
                    case "LUT_1D_INPUT_RANGE":
                    case "LUT_3D_INPUT_RANGE":
                        if (parts.Length != 3) throw Error(lineNumber, "Expected two input range values.");
                        var low = ReadFloat(parts[1], lineNumber);
                        var high = ReadFloat(parts[2], lineNumber);
                        min = new Rgb(low, low, low);
                        max = new Rgb(high, high, high);
                        break;
                    default:
                        throw Error(lineNumber, $"Unsupported header: {key}.");
                }
            }
            else
            {
                if (size == 0) throw Error(lineNumber, "Missing LUT_1D_SIZE or LUT_3D_SIZE.");
                data ??= new Rgb[is3D ? checked(size * size * size) : size];
                if (count == data.Length) throw Error(lineNumber, "Too many table values.");
                data[count++] = ReadRgb(parts, 0, lineNumber);
            }
        }

        if (data is null || count != data.Length) throw Error(lineNumber, "Missing or incomplete table values.");
        var rangeKey = is3D ? "LUT_3D_INPUT_RANGE" : "LUT_1D_INPUT_RANGE";
        var wrongRangeKey = is3D ? "LUT_1D_INPUT_RANGE" : "LUT_3D_INPUT_RANGE";
        if (headers.Contains(wrongRangeKey) || headers.Contains(rangeKey) && (headers.Contains("DOMAIN_MIN") || headers.Contains("DOMAIN_MAX")))
            throw Error(lineNumber, "Conflicting input domain headers.");
        if (!(min.Red < max.Red && min.Green < max.Green && min.Blue < max.Blue))
            throw Error(lineNumber, "Every domain maximum must exceed its minimum.");
        return new CubeLut(title, size, is3D, min, max, data);
    }

    /// <summary>Maps a floating-point RGB value. Coordinates outside the input domain clamp to the edge.</summary>
    public Rgb Transform(Rgb input)
    {
        var r = Coordinate(input.Red, this.domainMin.Red, this.domainMax.Red, this.Size);
        var g = Coordinate(input.Green, this.domainMin.Green, this.domainMax.Green, this.Size);
        var b = Coordinate(input.Blue, this.domainMin.Blue, this.domainMax.Blue, this.Size);
        var r0 = Math.Min((int)r, this.Size - 2);
        var g0 = Math.Min((int)g, this.Size - 2);
        var b0 = Math.Min((int)b, this.Size - 2);
        r -= r0; g -= g0; b -= b0;
        if (!this.Is3D)
        {
            return new Rgb(
                Lerp(this.values[r0].Red, this.values[r0 + 1].Red, r),
                Lerp(this.values[g0].Green, this.values[g0 + 1].Green, g),
                Lerp(this.values[b0].Blue, this.values[b0 + 1].Blue, b));
        }

        // .cube ordering: red changes fastest, then green, then blue.
        var stride = this.Size;
        var plane = stride * stride;
        var i = r0 + stride * g0 + plane * b0;
        return Mix(
            Mix(Mix(this.values[i], this.values[i + 1], r), Mix(this.values[i + stride], this.values[i + stride + 1], r), g),
            Mix(Mix(this.values[i + plane], this.values[i + plane + 1], r), Mix(this.values[i + plane + stride], this.values[i + plane + stride + 1], r), g), b);
    }

    /// <summary>Applies the LUT after tone mapping. pixelScale is 1 for normalized pixels or 255 for RGB byte-range floats.</summary>
    public void ApplyInPlace(Rgb[] pixels, float strength = 1f, float pixelScale = 1f)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ValidateStrength(strength);
        if (!float.IsFinite(pixelScale) || pixelScale <= 0) throw new ArgumentOutOfRangeException(nameof(pixelScale));
        if (strength == 0) return;
        Parallel.For(0, pixels.Length, i =>
        {
            var source = pixels[i] / pixelScale;
            pixels[i] = Mix(source, this.Transform(source), strength) * pixelScale;
        });
    }

    /// <summary>Applies the LUT to an encoded RGB image, after HDR tone mapping and camera-response encoding.</summary>
    public void ApplyInPlace(IImageProxy image, float strength = 1f)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateStrength(strength);
        if (strength == 0) return;
        for (var y = 0; y < image.Height; y++)
        {
            var row = image.LoadRow(y);
            for (var x = 0; x < row.Length; x += 3)
            {
                var source = new Rgb(row[x] / 255f, row[x + 1] / 255f, row[x + 2] / 255f);
                var value = Mix(source, this.Transform(source), strength);
                row[x] = ToByte(value.Red); row[x + 1] = ToByte(value.Green); row[x + 2] = ToByte(value.Blue);
            }

            image.SaveRow(y, row);
        }
    }

    internal static void ValidateStrength(float strength)
    {
        if (!float.IsFinite(strength) || strength < 0 || strength > 1) throw new ArgumentOutOfRangeException(nameof(strength));
    }

    private static float Coordinate(float value, float min, float max, int size) =>
        (float)(Math.Clamp((double)(float.IsNaN(value) ? min : value) - min, 0d, (double)max - min) / ((double)max - min) * (size - 1));
    private static float Lerp(float a, float b, float t) => (float)((double)a * (1d - t) + (double)b * t);
    internal static Rgb Mix(Rgb a, Rgb b, float t) => new(Lerp(a.Red, b.Red, t), Lerp(a.Green, b.Green, t), Lerp(a.Blue, b.Blue, t));
    private static byte ToByte(float value) => (byte)Math.Clamp(MathF.Round(value * 255f), 0, 255);
    private static FormatException Error(int line, string message) => new($"Invalid .cube LUT at line {line}: {message}");
    private static float ReadFloat(string text, int line)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value))
            throw Error(line, $"Invalid finite number: {text}.");
        return value;
    }

    private static Rgb ReadRgb(string[] parts, int start, int line)
    {
        if (parts.Length != start + 3) throw Error(line, "Expected three RGB values.");
        return new Rgb(ReadFloat(parts[start], line), ReadFloat(parts[start + 1], line), ReadFloat(parts[start + 2], line));
    }
}
