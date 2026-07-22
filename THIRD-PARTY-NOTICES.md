# Third-Party Notices

HDRLib uses the projects listed below. NuGet restores these dependencies under
their own licenses; those licenses are not replaced by the HDRLib license.
Versions are the versions referenced by the `1.0.0` package project.

## Runtime dependencies

| Project | Version | Use | License |
| --- | ---: | --- | --- |
| [ILGPU](https://github.com/m4rs-mt/ILGPU) | 1.5.3 | Accelerator discovery, memory management, and GPU kernels | [MIT](https://spdx.org/licenses/MIT.html) |
| [ILGPU.Algorithms](https://github.com/m4rs-mt/ILGPU) | 1.5.3 | ILGPU algorithm extensions | [MIT](https://spdx.org/licenses/MIT.html) |
| [MetadataExtractor for .NET](https://github.com/drewnoakes/metadata-extractor-dotnet) | 2.8.1 | Image metadata and EXIF support | [Apache-2.0](https://spdx.org/licenses/Apache-2.0.html) |
| [XmpCore for .NET](https://github.com/drewnoakes/xmp-core-dotnet) | 6.1.10.1 | Transitive XMP metadata support | [BSD-3-Clause](https://spdx.org/licenses/BSD-3-Clause.html) |
| [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp) | 3.1.11 | Managed image decoding, encoding, pixels, and metadata | [Six Labors Split License 1.0](https://github.com/SixLabors/ImageSharp/blob/v3.1.11/LICENSE) |
| [.NET runtime libraries](https://github.com/dotnet/runtime) | package-dependent | Transitive platform, unsafe, and text-encoding support | [MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |

The `.NET runtime libraries` row covers the transitive packages currently
resolved as `Microsoft.NETCore.Platforms`, `System.Runtime.CompilerServices.Unsafe`,
and `System.Text.Encoding.CodePages`.

### ImageSharp licensing note

ImageSharp 3.x is **not** Apache-2.0. It is distributed under the Six Labors
Split License, Version 1.0. Its terms distinguish between categories of users
and between direct and transitive use. Review the versioned upstream license
and obtain any required Six Labors commercial license before distributing or
using HDRLib in a closed-source commercial product.

## Test and build dependencies

These projects are used to build or test HDRLib and are not runtime dependencies
of the `HDRLib` NuGet package.

| Project | Use | License |
| --- | --- | --- |
| [Microsoft.NET.Test.Sdk / VSTest](https://github.com/microsoft/vstest) | Test host and platform | [MIT](https://github.com/microsoft/vstest/blob/main/LICENSE) |
| [NUnit](https://github.com/nunit/nunit) | Test framework | [MIT](https://github.com/nunit/nunit/blob/master/LICENSE.txt) |
| [NUnit3TestAdapter](https://github.com/nunit/nunit3-vs-adapter) | VSTest adapter | [MIT](https://github.com/nunit/nunit3-vs-adapter/blob/master/LICENSE) |
| [NUnit.Analyzers](https://github.com/nunit/nunit.analyzers) | Test analyzers | [MIT](https://github.com/nunit/nunit.analyzers/blob/master/LICENSE) |
| [coverlet](https://github.com/coverlet-coverage/coverlet) | Code coverage collector | [MIT](https://github.com/coverlet-coverage/coverlet/blob/master/LICENSE) |

This notice is informational and is not legal advice. The upstream license text
for each dependency controls.
