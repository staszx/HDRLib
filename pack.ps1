param(
    [string]$Configuration = "Release",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

if ([string]::IsNullOrEmpty($OutputDirectory)) {
    $OutputDirectory = Join-Path $scriptDir "artifacts"
}

$project = Join-Path $scriptDir "HDRLib" "HDRLib.csproj"
$nupkgDir = Join-Path (Split-Path $project -Parent) "bin" $Configuration

Write-Host "=== HDRLib NuGet Package Build ===" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration"
Write-Host "Output: $OutputDirectory"
Write-Host ""

# Clean previous artifacts
if (Test-Path $OutputDirectory) {
    Remove-Item -Path "$OutputDirectory\*" -Recurse -Force
}

# Build and pack
dotnet clean $project -c $Configuration --nologo -v q
if ($?) { dotnet build $project -c $Configuration --nologo -v q }
if ($?) {
    dotnet pack $project -c $Configuration --nologo -o $OutputDirectory --include-symbols -p:IncludeSymbols=true -p:SymbolPackageFormat=snupkg
}

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "=== Package created successfully ===" -ForegroundColor Green
    Get-ChildItem $OutputDirectory -Filter "*.nupkg" | ForEach-Object { Write-Host "  $($_.Name)" }
    Get-ChildItem $OutputDirectory -Filter "*.snupkg" | ForEach-Object { Write-Host "  $($_.Name)" }
} else {
    Write-Host "Pack failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}
