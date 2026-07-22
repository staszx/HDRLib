using HDRLib;
using HDRLib.Align;
using HDRLib.Gpu;
using HDRLib.Hdr.Debevec;
using HDRLib.Interfaces;
using HDRLib.PixelProvider.ImageSharp;
using HDRLib.ToneMapping.Settings;

if (args.Length < 3)
{
    PrintUsage();
    return 1;
}

var mode = args[0].ToLowerInvariant();
var outputPath = args[1];

switch (mode)
{
    case "hdr-auto" when args.Length >= 4:
        ProcessHdr(args[2..], outputPath, useGpu: false);
        break;
    case "hdr-gpu" when args.Length >= 4:
        ProcessHdr(args[2..], outputPath, useGpu: true);
        break;
    case "single-auto":
        ProcessSingle(args[2], outputPath, useGpu: false);
        break;
    case "single-gpu":
        ProcessSingle(args[2], outputPath, useGpu: true);
        break;
    default:
        PrintUsage();
        return 1;
}

Console.WriteLine($"Saved: {Path.GetFullPath(outputPath)}");
return 0;

static void ProcessHdr(string[] inputPaths, string outputPath, bool useGpu)
{
    SystemHelper.UseAvxState = UseAvxState.Auto;
    using var gpu = useGpu ? new GpuContext() : null;
    var exposures = new List<IImageProxy>();

    try
    {
        foreach (var inputPath in inputPaths)
        {
            var image = new ImageSharpProxy();
            image.Load(inputPath);
            exposures.Add(image);
        }

        var aligner = gpu is null ? ImageAligner.Create() : ImageAligner.Create(gpu);
        aligner.Process(exposures);

        var processor = gpu is null
            ? new HDRProcessor<ImageSharpProxy>()
            : new HDRProcessor<ImageSharpProxy>(gpu);

        using var output = processor.Process(exposures, new HdrImageOptions
        {
            SampleCount = 1_000,
            SmoothFactor = 300,
            MotionFilterStrength = 80,
            ToneMapperSettings = CreateSettings()
        });

        EnsureOutputDirectory(outputPath);
        output.SaveAsJpeg(outputPath);
    }
    finally
    {
        foreach (var exposure in exposures)
        {
            exposure.Dispose();
        }
    }
}

static void ProcessSingle(string inputPath, string outputPath, bool useGpu)
{
    SystemHelper.UseAvxState = UseAvxState.Auto;
    using var gpu = useGpu ? new GpuContext() : null;
    using var source = new ImageSharpProxy();
    source.Load(inputPath);

    using var processor = gpu is null
        ? new SingleImageProcessor(source)
        : new SingleImageProcessor(gpu);

    if (gpu is not null)
    {
        processor.LoadSource(source);
    }

    processor.Process(CreateSettings());
    using var output = processor.ToImage<ImageSharpProxy>();
    EnsureOutputDirectory(outputPath);
    output.SaveAsJpeg(outputPath);
}

static NaturalToneMapperSettings CreateSettings() => new()
{
    AutoAdjustEnabled = true,
    Contrast = 1.1f,
    Gamma = 1f
};

static void EnsureOutputDirectory(string outputPath)
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }
}

static void PrintUsage()
{
    Console.WriteLine("HDRLib examples");
    Console.WriteLine("  dotnet run --project samples/HDRLib.Examples -- hdr-auto <output.jpg> <dark.jpg> <mid.jpg> <light.jpg>");
    Console.WriteLine("  dotnet run --project samples/HDRLib.Examples -- hdr-gpu <output.jpg> <dark.jpg> <mid.jpg> <light.jpg>");
    Console.WriteLine("  dotnet run --project samples/HDRLib.Examples -- single-auto <output.jpg> <input.jpg>");
    Console.WriteLine("  dotnet run --project samples/HDRLib.Examples -- single-gpu <output.jpg> <input.jpg>");
}
