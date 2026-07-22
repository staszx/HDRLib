# HDRLib

![HDRLib icon](https://raw.githubusercontent.com/staszx/HDRLib/master/assets/HDRLib.png)

HDRLib is a .NET 8 library for merging exposure brackets into display-ready
high-dynamic-range images and for applying the same tone-mapping pipeline to a
single image. It combines automatic AVX2 SIMD acceleration on supported CPUs
with an explicit ILGPU path for compatible accelerators.

- Debevec-style camera response recovery and radiance-map construction
- Pyramid image alignment before exposure merging
- Motion masking to reduce ghosts in moving regions
- Natural, ACES Filmic, brightness-balancing, and contrast-balancing tone mappers
- Automatic AVX2 selection with a scalar/parallel CPU fallback
- Explicit GPU execution through ILGPU
- JPEG/PNG image I/O and EXIF exposure metadata through ImageSharp

Source, issues, and releases: [github.com/staszx/HDRLib](https://github.com/staszx/HDRLib)

## Install

```powershell
dotnet add package HDRLib --version 1.0.0
```

The package targets `net8.0` and contains both `HDRLib.dll` and the
`HDRLib.PixelProvider.ImageSharp.dll` image provider used below.

## How the HDR algorithm works

HDR processing starts with two or more photographs of the same scene at
different exposure times:

1. **Load exposure data.** The ImageSharp provider reads RGB pixels and EXIF
   exposure metadata. Accurate exposure times are important for radiance recovery.
2. **Align the bracket.** A coarse-to-fine image pyramid estimates the movement
   between frames and brings them into a common coordinate system.
3. **Detect movement.** A motion mask identifies regions that do not agree across
   the bracket so moving subjects contribute less to the merge.
4. **Recover the camera response.** Stratified samples across the luminance range
   are used to solve a smooth Debevec response curve for each RGB channel.
5. **Build the radiance map.** Well-exposed samples receive more weight than
   clipped shadows or highlights. The weighted values are combined into a
   floating-point scene-radiance image.
6. **Normalize and tone-map.** The HDR values are normalized and passed through
   the selected tone mapper, producing an ordinary image that retains useful
   highlight and shadow detail.

`SampleCount` controls the number of points used to estimate the response curve.
`SmoothFactor` regularizes that curve. `MotionFilterStrength` controls motion-mask
sensitivity from `0` (disabled) to `100`; it is not a generic sharpening control.

## Hardware acceleration

The default CPU path uses `Parallel.For`. With
`SystemHelper.UseAvxState = UseAvxState.Auto` (the default), HDRLib selects its
AVX2 implementation when `Avx2.IsSupported`; otherwise it uses the scalar CPU
implementation. Keep `Auto` unless you are deliberately testing a particular
path—forcing SIMD on unsupported hardware is invalid.

GPU execution is explicit: create one `GpuContext` and pass it to the aligner,
HDR processor, or single-image processor. Reuse the context across images so
ILGPU kernels and accelerator resources are not recreated for every file. ILGPU
enumerates the accelerators available through its installed backends; actual GPU
support depends on the machine, driver, and backend.

Strong naming and GPU acceleration are independent: the NuGet assemblies have
public key token `eaeefe50e84677d9`; strong naming provides assembly identity,
not publisher authentication or NuGet repository signing.

## HDR example: automatic CPU/SIMD selection

```csharp
using HDRLib;
using HDRLib.Align;
using HDRLib.Hdr.Debevec;
using HDRLib.Interfaces;
using HDRLib.PixelProvider.ImageSharp;
using HDRLib.ToneMapping.Settings;

SystemHelper.UseAvxState = UseAvxState.Auto;

var exposures = new List<IImageProxy>();
try
{
    foreach (var path in new[] { "scene-2ev.jpg", "scene-0ev.jpg", "scene+2ev.jpg" })
    {
        var image = new ImageSharpProxy();
        image.Load(path);
        exposures.Add(image);
    }

    ImageAligner.Create().Process(exposures);

    var processor = new HDRProcessor<ImageSharpProxy>();
    using var output = processor.Process(exposures, new HdrImageOptions
    {
        SampleCount = 1_000,
        SmoothFactor = 300,
        MotionFilterStrength = 80,
        ToneMapperSettings = new NaturalToneMapperSettings
        {
            AutoAdjustEnabled = true,
            Gamma = 1f
        }
    });

    output.SaveAsJpeg("scene-hdr.jpg");
}
finally
{
    foreach (var image in exposures)
        image.Dispose();
}
```

## HDR example: GPU alignment and merge

```csharp
using HDRLib;
using HDRLib.Align;
using HDRLib.Gpu;
using HDRLib.Hdr.Debevec;
using HDRLib.Interfaces;
using HDRLib.PixelProvider.ImageSharp;
using HDRLib.ToneMapping.Settings;

using var gpu = new GpuContext(); // preferred accelerator
var exposures = new List<IImageProxy>();
try
{
    foreach (var path in new[] { "room-dark.jpg", "room-mid.jpg", "room-light.jpg" })
    {
        var image = new ImageSharpProxy();
        image.Load(path);
        exposures.Add(image);
    }

    ImageAligner.Create(gpu).Process(exposures);

    var processor = new HDRProcessor<ImageSharpProxy>(gpu);
    using var output = processor.Process(exposures, new HdrImageOptions
    {
        SampleCount = 1_500,
        SmoothFactor = 400,
        MotionFilterStrength = 70,
        ToneMapperSettings = new NaturalToneMapperSettings
        {
            AutoAdjustEnabled = true,
            Contrast = 1.1f,
            Gamma = 1f
        }
    });

    output.SaveAsJpeg("room-hdr-gpu.jpg");
}
finally
{
    foreach (var image in exposures)
        image.Dispose();
}
```

If the default accelerator is not the one you want, inspect
`GpuContext.GetAccelerators()` and pass its zero-based index to
`new GpuContext(index)`.

## SINGLE example: automatic CPU/SIMD tone mapping

SINGLE processing does not create new scene dynamic range. It applies HDRLib's
tone-mapping and post-processing operators to one ordinary image.

```csharp
using HDRLib;
using HDRLib.Hdr.Debevec;
using HDRLib.PixelProvider.ImageSharp;
using HDRLib.ToneMapping.Settings;

SystemHelper.UseAvxState = UseAvxState.Auto;

using var source = new ImageSharpProxy();
source.Load("portrait.jpg");

using var processor = new SingleImageProcessor(source);
processor.Process(new NaturalToneMapperSettings
{
    AutoAdjustEnabled = true,
    ExposureEV = 0.25f,
    Contrast = 1.1f,
    Saturation = 5f,
    Gamma = 1f
});

using var output = processor.ToImage<ImageSharpProxy>();
output.SaveAsJpeg("portrait-tonemapped.jpg");
```

## SINGLE example: reusable GPU processor

```csharp
using HDRLib.Gpu;
using HDRLib.Hdr.Debevec;
using HDRLib.PixelProvider.ImageSharp;
using HDRLib.ToneMapping.Settings;

using var gpu = new GpuContext();
using var processor = new SingleImageProcessor(gpu);

var settings = new AcesFilmicTonemapperSettings
{
    AutoAdjustEnabled = true,
    ExposureEV = 0.2f,
    Saturation = 4f
};

foreach (var path in Directory.EnumerateFiles("input", "*.jpg"))
{
    using var source = new ImageSharpProxy();
    source.Load(path);

    processor.LoadSource(source);
    processor.Process(settings);

    using var output = processor.ToImage<ImageSharpProxy>();
    output.SaveAsJpeg(Path.Combine("output", Path.GetFileName(path)));
}
```

## Licensing

HDRLib is available under **AGPL-3.0-or-later**. A separate commercial license
is required when you cannot or do not want to comply with the AGPL obligations.
See [LICENSE](https://github.com/staszx/HDRLib/blob/master/LICENSE).

HDRLib also depends on projects with their own terms. In particular,
ImageSharp 3.1.11 uses the Six Labors Split License rather than Apache-2.0 and
may require a Six Labors commercial license in some closed-source commercial
scenarios. Review [THIRD-PARTY-NOTICES.md](https://github.com/staszx/HDRLib/blob/master/THIRD-PARTY-NOTICES.md)
before distribution.

## Build and pack

```powershell
dotnet test HDRLib.sln -c Release
.\pack.ps1 -Configuration Release
```

The package and symbols package are written to `artifacts/`. Both library
assemblies are strong-name signed from `HDRLib.snk`.

The four snippets above are also available as a buildable console project in
[`samples/HDRLib.Examples`](https://github.com/staszx/HDRLib/tree/master/samples/HDRLib.Examples).
