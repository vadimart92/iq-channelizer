param(
    [Parameter(Mandatory = $true)]
    [string] $FftwRuntimePath,
    [string] $Configuration = "Release",
    [string] $ReportPath = "artifacts/package-validation.json"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$libraryProject = Join-Path $repositoryRoot "src/IqChannelizer/IqChannelizer.csproj"
$runtimePath = [System.IO.Path]::GetFullPath($FftwRuntimePath)
$reportFullPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $ReportPath))
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("IqChannelizer-package-validation-" + [Guid]::NewGuid().ToString("N"))
$packageDirectory = Join-Path $temporaryRoot "packages"
$consumerDirectory = Join-Path $temporaryRoot "consumer"
$consumerPackagesDirectory = Join-Path $temporaryRoot "consumer-packages"

function Invoke-DotNet([string[]] $Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath $runtimePath -PathType Leaf)) {
    throw "The independently supplied FFTW runtime was not found: $runtimePath"
}

New-Item -ItemType Directory -Path $packageDirectory, $consumerDirectory -Force | Out-Null
try {
    Invoke-DotNet @(
        "pack", $libraryProject,
        "-c", $Configuration,
        "--no-restore",
        "-o", $packageDirectory
    )

    $packages = @(Get-ChildItem -LiteralPath $packageDirectory -Filter "IqChannelizer.*.nupkg")
    if ($packages.Count -ne 1) {
        throw "Expected exactly one IqChannelizer package, found $($packages.Count)."
    }

    $package = $packages[0]
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName })
    }
    finally {
        $archive.Dispose()
    }

    $forbiddenEntries = @($entries | Where-Object {
        $_ -match '(?i)(^|/)libfftw[^/]*\.(dll|lib|a)$' -or
        $_ -match '(?i)(^|/)fftw[^/]*\.h$'
    })
    if ($forbiddenEntries.Count -ne 0) {
        throw "Managed-only package contains forbidden FFTW assets: $($forbiddenEntries -join ', ')"
    }

    foreach ($requiredEntry in @("LICENSE", "README.md", "lib/net10.0/IqChannelizer.dll")) {
        if ($entries -notcontains $requiredEntry) {
            throw "Package is missing required entry '$requiredEntry'."
        }
    }

    $nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="validation" value="$packageDirectory" />
  </packageSources>
</configuration>
"@
    Set-Content -LiteralPath (Join-Path $consumerDirectory "NuGet.Config") -Value $nugetConfig

    $consumerProject = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="IqChannelizer" Version="0.1.0" />
  </ItemGroup>
</Project>
"@
    $consumerProjectPath = Join-Path $consumerDirectory "Consumer.csproj"
    Set-Content -LiteralPath $consumerProjectPath -Value $consumerProject

    $consumerProgram = @"
using IqChannelizer;
using IqChannelizer.Abstractions;

var request = new ChannelizerRequest(
    1024,
    [new ChannelRequest(7, 0, 20, 20, 50, 0.2)],
    ChannelizerStrategy.Fdc,
    new InputBlockConstraints(128, 128),
    new ChannelizerImplementationHints(FdcDecimationFactor: 8, Simd: SimdPreference.Scalar));
using var engine = ChannelizerFactory.Create(request);
var input = new ComplexF[engine.InputRequirements.InputSize];
engine.Process(input, 0, new Sink());
Console.WriteLine($"validated:{engine.Plan.Strategy}:{engine.Plan.Channels.Count}");

sealed class Sink : IChannelOutputSink
{
    public void Write(int channelId, ReadOnlySpan<ComplexF> samples)
    {
        if (channelId != 7 || samples.IsEmpty)
        {
            throw new InvalidOperationException("Unexpected package-consumer output.");
        }
    }
}
"@
    Set-Content -LiteralPath (Join-Path $consumerDirectory "Program.cs") -Value $consumerProgram

    Invoke-DotNet @(
        "restore", $consumerProjectPath,
        "--configfile", (Join-Path $consumerDirectory "NuGet.Config"),
        "--packages", $consumerPackagesDirectory,
        "--no-cache"
    )
    Invoke-DotNet @("build", $consumerProjectPath, "-c", $Configuration, "--no-restore")

    $consumerOutput = Join-Path $consumerDirectory "bin/$Configuration/net10.0"
    Copy-Item -LiteralPath $runtimePath -Destination (Join-Path $consumerOutput "libfftw3f-3.dll")
    $executionOutput = & dotnet (Join-Path $consumerOutput "Consumer.dll")
    if ($LASTEXITCODE -ne 0 -or $executionOutput -notcontains "validated:Fdc:1") {
        throw "Clean consumer execution failed: $($executionOutput -join [Environment]::NewLine)"
    }

    $reportDirectory = Split-Path -Parent $reportFullPath
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    $report = [ordered]@{
        schemaVersion = 1
        status = "passed"
        generatedOn = (Get-Date).ToString("yyyy-MM-dd")
        package = $package.Name
        packageSha256 = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        requiredEntries = @("LICENSE", "README.md", "lib/net10.0/IqChannelizer.dll")
        forbiddenFftwEntries = @()
        consumerTargetFramework = "net10.0"
        packageSource = "isolated-local-directory"
        nativeRuntimeBundled = $false
        separatelySuppliedRuntime = [ordered]@{
            fileName = [System.IO.Path]::GetFileName($runtimePath)
            sha256 = (Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        consumerExecution = "validated:Fdc:1"
    }
    Set-Content -LiteralPath $reportFullPath -Value ($report | ConvertTo-Json -Depth 5)
    Write-Output $reportFullPath
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
