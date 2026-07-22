param(
    [string]$Configuration = "Release",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$expectedPublicKeyToken = "eaeefe50e84677d9"

if ([string]::IsNullOrEmpty($OutputDirectory)) {
    $OutputDirectory = Join-Path $scriptDir "artifacts"
}

$project = Join-Path $scriptDir "HDRLib\HDRLib.csproj"
$coreAssembly = Join-Path $scriptDir "HDRLib\bin\$Configuration\net8.0\HDRLib.dll"
$providerAssembly = Join-Path $scriptDir "HDRLib.PixelProvider.ImageSharp\bin\$Configuration\net8.0\HDRLib.PixelProvider.ImageSharp.dll"

function Invoke-DotNet {
    param([string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Assert-StrongName {
    param([string]$AssemblyPath)

    $assemblyName = [System.Reflection.AssemblyName]::GetAssemblyName($AssemblyPath)
    $tokenBytes = $assemblyName.GetPublicKeyToken()
    $token = ($tokenBytes | ForEach-Object { $_.ToString("x2") }) -join ""
    if ($token -ne $expectedPublicKeyToken) {
        throw "Unexpected public key token '$token' in '$AssemblyPath'. Expected '$expectedPublicKeyToken'."
    }

    Write-Host "  $($assemblyName.Name): $token" -ForegroundColor DarkGreen
}

Write-Host "=== HDRLib NuGet Package Build ===" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration"
Write-Host "Output: $OutputDirectory"
Write-Host "Strong-name token: $expectedPublicKeyToken"
Write-Host ""

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $OutputDirectory -Force | Remove-Item -Recurse -Force

Invoke-DotNet @("clean", $project, "-c", $Configuration, "--nologo", "-v", "q")
Invoke-DotNet @("build", $project, "-c", $Configuration, "--nologo", "-v", "q")
Invoke-DotNet @(
    "pack", $project,
    "-c", $Configuration,
    "--nologo",
    "-o", $OutputDirectory,
    "--include-symbols",
    "-p:IncludeSymbols=true",
    "-p:SymbolPackageFormat=snupkg"
)

Write-Host ""
Write-Host "Strong-name identities:" -ForegroundColor Cyan
Assert-StrongName $coreAssembly
Assert-StrongName $providerAssembly

$packages = Get-ChildItem -LiteralPath $OutputDirectory -Filter "*.nupkg"
if ($packages.Count -eq 0) {
    throw "No NuGet package was created in '$OutputDirectory'."
}

Write-Host ""
Write-Host "=== Package created successfully ===" -ForegroundColor Green
Get-ChildItem -LiteralPath $OutputDirectory -Include "*.nupkg", "*.snupkg" -File |
    ForEach-Object { Write-Host "  $($_.Name)" }
