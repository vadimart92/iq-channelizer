[CmdletBinding()]
param(
    [string] $Configuration = "Release",
    [string] $RuntimeIdentifier = "win-x64",
    [string] $OutputRoot = "artifacts/distributions"
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectPath = Join-Path $repositoryRoot "benchmarks/IqChannelizer.Benchmarks/IqChannelizer.Benchmarks.csproj"
$collectorPath = Join-Path $repositoryRoot "build/Collect-VTune.ps1"
$readmePath = Join-Path $repositoryRoot "build/VTune-Package-README.md"
$resolvedOutputRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot))

$commit = (& git -C $repositoryRoot rev-parse --short=12 HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) {
    $commit = "unknown"
}
$dirty = -not [string]::IsNullOrWhiteSpace((& git -C $repositoryRoot status --porcelain | Out-String))
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$packageName = "IqChannelizer-VTune-$RuntimeIdentifier-$commit-$stamp"
$packageDirectory = Join-Path $resolvedOutputRoot $packageName
$applicationDirectory = Join-Path $packageDirectory "app"
$archivePath = Join-Path $resolvedOutputRoot "$packageName.zip"

if ((Test-Path -LiteralPath $packageDirectory) -or (Test-Path -LiteralPath $archivePath)) {
    throw "Refusing to overwrite an existing package: $packageName"
}

New-Item -ItemType Directory -Path $applicationDirectory -Force | Out-Null

& dotnet publish $projectPath `
    -t:Rebuild `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=false `
    -p:DebugSymbols=true `
    -p:DebugType=portable `
    -p:Optimize=true `
    -o $applicationDirectory
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

foreach ($requiredFile in @(
    "IqChannelizer.Benchmarks.exe",
    "IqChannelizer.Benchmarks.dll",
    "IqChannelizer.Benchmarks.pdb",
    "IqChannelizer.pdb",
    "libfftw3f-3.dll",
    "coreclr.dll"
)) {
    $requiredPath = Join-Path $applicationDirectory $requiredFile
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Published package is missing required file: $requiredFile"
    }
}

foreach ($pdbFile in @("IqChannelizer.Benchmarks.pdb", "IqChannelizer.pdb")) {
    $pdbPath = Join-Path $applicationDirectory $pdbFile
    $pdbBytes = [System.IO.File]::ReadAllBytes($pdbPath)
    $pdbMagic = if ($pdbBytes.Length -ge 4) {
        [System.Text.Encoding]::ASCII.GetString($pdbBytes, 0, 4)
    }
    else {
        ""
    }

    if ($pdbMagic -ne "BSJB") {
        throw "Published symbol file is not a Portable PDB: $pdbFile"
    }
}

Copy-Item -LiteralPath $collectorPath -Destination (Join-Path $packageDirectory "Collect-VTune.ps1")
Copy-Item -LiteralPath $readmePath -Destination (Join-Path $packageDirectory "README.md")

$files = @(Get-ChildItem -LiteralPath $packageDirectory -File -Recurse | ForEach-Object {
    [ordered]@{
        path = [System.IO.Path]::GetRelativePath($packageDirectory, $_.FullName).Replace('\', '/')
        length = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$manifest = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    packageName = $packageName
    commit = $commit
    sourceWorktreeDirty = $dirty
    configuration = $Configuration
    targetFramework = "net10.0"
    runtimeIdentifier = $RuntimeIdentifier
    selfContained = $true
    publishSingleFile = $false
    publishReadyToRun = $false
    optimize = $true
    debugType = "portable"
    workload = [ordered]@{
        inputSampleRateHz = 100000000
        channelCount = 100
    }
    files = $files
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $packageDirectory "package-manifest.json") -Encoding UTF8

Compress-Archive -Path (Join-Path $packageDirectory "*") -DestinationPath $archivePath -CompressionLevel Optimal

$result = [ordered]@{
    packageDirectory = $packageDirectory
    archivePath = $archivePath
    archiveSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    archiveLength = (Get-Item -LiteralPath $archivePath).Length
}
$result | ConvertTo-Json -Depth 3
