# Architecture

## Overview

HDRLib processes multiple exposure images into a single HDR tone-mapped image through a pipeline:

```text
Input Images -> Alignment -> Radiance Map -> Tone Mapping -> Output Image
```

## Projects

| Project | Description |
|---------|-------------|
| `HDRLib` | Core library: HDR creation, tone mapping, alignment, image processing |
| `HDRLib.PixelProvider.ImageSharp` | Image I/O via SixLabors.ImageSharp |
| `PhotoProcessor` | CLI batch processor |

The desktop GUI is maintained separately in the sibling `ImageProcessor`
solution and references this library as a project dependency.

## Processing Pipeline

### 1. Alignment (optional)

Corrects translation and rotation between exposures using a pyramid-based
approach. The default factory selects the available implementation for the
current hardware.

### 2. Radiance Map Construction

Uses Debevec's algorithm to merge weighted samples from multiple exposures into a single floating-point HDR image.

### 3. Tone Mapping

Converts HDR data to display-ready LDR. Supported operators:

- **ACES Filmic**: Industry-standard filmic curve
- **Natural Tone Mapper**: Perceptual mapping
- **Brightness Balancer**: Adaptive brightness/contrast
- **Contrast Balancer**: Local contrast enhancement
- **Auto Adjust**: Histogram-based automatic adjustment

Each operator has three implementations:

- `ToneMapper` (CPU, `Parallel.For`)
- `ToneMapperSIMD` (AVX2/AVX-512)
- `ToneMapperGpu` (ILGPU kernels)

## Hardware Selection

`SystemHelper.UseAvx` determines SIMD availability. The factory methods automatically select the best available implementation:

```text
ImageAligner.Create()         -> SIMD if AVX available, else Classic
ToneMapperFactory.Create()    -> SIMD if AVX available, else CPU
```

GPU mode is explicit:

```csharp
ImageAligner.Create(gpuContext)
new HDRProcessor<T>(gpuContext)
```

## Key Interfaces

- `IImageProxy` - Abstract image access (load rows, save, clone)
- `IHdrImageProcessor` - Common processing contract
- `IToneMapperGpu` - GPU tone mapper contract
