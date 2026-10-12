# Cube LUT color grading

`HDRLib.ColorGrading.CubeLut` loads immutable `.cube` tables from a file or stream.
It supports standalone 1D (2–65536 samples) and 3D (2–256 samples per axis)
tables, quoted titles, comments, `DOMAIN_MIN` / `DOMAIN_MAX`, and matching
`LUT_1D_INPUT_RANGE` / `LUT_3D_INPUT_RANGE` headers. Combined 1D shaper + 3D
files are rejected with a format error. Numbers use invariant culture.

1D tables interpolate each channel linearly. 3D tables use trilinear
interpolation with red changing fastest, following the
[IRIDAS Cube format](https://github.com/AcademySoftwareFoundation/OpenColorIO/blob/main/src/OpenColorIO/fileformats/FileFormatIridasCube.cpp).
Input outside the domain clamps to its edge. Float output remains unclipped;
conversion to an output image clamps to the byte range.

```csharp
var lut = CubeLut.Load("look.cube"); // Load once; reuse for all frames.
using var processor = new SingleImageProcessor(source);
processor.ProcessWithLut(toneMapperSettings, lut, strength: 0.75f);
using var result = processor.ToImage<ImageSharpProxy>();
```

For the HDR pipeline, set `HdrImageOptions.OutputLut` and
`OutputLutStrength` (0–1, default 1). This applies the LUT after tone mapping
and camera-response encoding. `CubeLut.ApplyInPlace` also accepts normalized
float RGB arrays, byte-range float RGB arrays (`pixelScale: 255`), or an
`IImageProxy`. `Transform` maps a single floating-point RGB value.

`ProcessWithLut` and `HDRProcessor` select the LUT backend automatically:
AVX2 when enabled/available, scalar otherwise, and ILGPU with a GPU context.
The GUI's existing Accelerator selection controls both tone mapping and LUT.

`CubeLutSIMD.ApplyInPlace(lut, pixels, strength, pixelScale)` exposes AVX2
lookup and interpolation directly, including scalar fallback and tails.
It also accepts an `IImageProxy`.

`CubeLutGpu` owns a device LUT table. Construct it with `(context, lut)` and
call `ApplyInPlace(devicePixels, strength, pixelScale)` on an existing device
RGB buffer. Dispose it before its context. `SingleImageProcessor` caches
this table across calls and releases it when replacing the LUT or disposing
the processor. GPU LUT processing stays on the device before the normal
output readback. HDR preserves the existing encoded-byte LUT input contract.

The LUT stage does not guess or convert a LUT's intended color space or gamma:
use a LUT intended for the output image's RGB encoding.

In the GUI, use **Cube LUT — after tone mapper** to load a file, enable it,
or change its strength. The selection is saved in application settings,
independently of tone-mapper XML presets. **Show original** bypasses both
the tone mapper and LUT. A batch captures its LUT and strength when started.
