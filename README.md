# HDRLib

A high-performance C# library for creating and processing High Dynamic Range (HDR) images.
Supports CPU, SIMD (AVX2/AVX-512), and GPU (CUDA/OpenCL via ILGPU) processing pipelines.

## Features

- **HDR Creation** - Merge multiple exposures into a single HDR radiance map using Debevec's algorithm
- **Image Alignment** - Pyramid-based alignment with sub-pixel accuracy; corrects translation and rotation
- **Tone Mapping** - Multiple tone mapping operators:
  - ACES Filmic (industry-standard filmic curve)
  - Natural Tone Mapper (perceptual)
  - Brightness Balancer
  - Contrast Balancer
  - Auto Adjust (histogram-based)
- **White Balance** - Automatic and manual white balance correction
- **Post-Processing** - Tone boost, dehaze, local contrast, color temperature, blending
- **Cross-Platform** - Windows x64, Linux x64, macOS x64
- **Hardware Acceleration** - Auto-selection between pure CPU, AVX2/AVX-512 SIMD, and GPU kernels

## Quick Start

```csharp
using HDRLib;
using HDRLib.ToneMapping.Settings;
using HDRLib.PixelProvider.ImageSharp;

// Load images
var images = new List<IImageProxy>();
foreach (var path in new[] { "img1.jpg", "img2.jpg", "img3.jpg" })
{
    var proxy = new ImageSharpProxy();
    proxy.Load(path);
    images.Add(proxy);
}

// Align (optional)
var aligner = ImageAligner.Create();
aligner.Process(images);

// Create HDR and apply tone mapping
var processor = new HDRProcessor<ImageSharpProxy>();
var result = processor.Process(images, new HdrImageOptions
{
    SampleCount = 1000,
    SmoothFactor = 300,
    MotionFilterStrength = 80,
    ToneMapperSettings = new NaturalToneMapperSettings
    {
        TargetGray = 0.26f,
        WhitePointPercentile = 0.98f,
        Gamma = 1.1f
    }
});

// Save result
result.SaveAsJpeg("output.jpg");
```

### GPU Acceleration

```csharp
using var gpu = new GpuContext();
var aligner = ImageAligner.Create(gpu);
var processor = new HDRProcessor<ImageSharpProxy>(gpu);
```

## Requirements

- .NET 8.0 SDK or later
- For GPU mode: NVIDIA CUDA-capable GPU or AMD GPU with OpenCL support

## Building

```powershell
dotnet build HDRLib.sln
```

## Testing

```powershell
dotnet test HDRLib.Tests
```

## NuGet Package

```powershell
dotnet pack HDRLib/HDRLib.csproj -c Release
```

The package will be created in `HDRLib/bin/Release/`.

## License

This project is licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0)**.
See [LICENSE](LICENSE) for details. Third-party package notices are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

**Commercial License:** If you need to use this library in a proprietary or
commercial product without disclosing your source code, a commercial license
is available from the author.
