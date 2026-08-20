[CmdletBinding()]
param(
    [ValidateSet("pfb", "fdc")]
    [string] $Strategy = "pfb",

    [ValidateSet("auto", "scalar", "avx2", "avx512")]
    [string] $Simd = "auto",

    [ValidateSet("conservative", "foldaware")]
    [string] $PfbDesign = "conservative",

    [ValidateSet("sw", "hw")]
    [string] $HotspotsSamplingMode = "sw",

    [int] $WarmupIterations = 256,
    [int] $Iterations = 5000,
    [string] $OutputRoot = "",
    [string] $VtunePath = "",
    [switch] $IncludeMicroarchitecture,
    [switch] $SystemInfoOnly
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

if ($WarmupIterations -lt 0) {
    throw "WarmupIterations must be non-negative."
}

if ($Iterations -lt 1) {
    throw "Iterations must be positive."
}

$packageRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$applicationPath = Join-Path $packageRoot "app/IqChannelizer.Benchmarks.exe"
$packageManifestPath = Join-Path $packageRoot "package-manifest.json"
if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf)) {
    throw "Profile target was not found: $applicationPath"
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $packageRoot "captures"
}
else {
    $OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
}

$captureName = "vtune-capture-{0}-{1}" -f (Get-Date -Format "yyyyMMdd-HHmmss"), [Guid]::NewGuid().ToString("N").Substring(0, 8)
$captureDirectory = Join-Path $OutputRoot $captureName
$logsDirectory = Join-Path $captureDirectory "logs"
$reportsDirectory = Join-Path $captureDirectory "reports"
New-Item -ItemType Directory -Path $captureDirectory, $logsDirectory, $reportsDirectory -Force | Out-Null
$commandLogPath = Join-Path $captureDirectory "commands.txt"

function Format-CommandLine {
    param([string] $Executable, [string[]] $Arguments)

    $escaped = @($Arguments | ForEach-Object {
        if ($_ -match '[\s"]') {
            '"' + ($_ -replace '"', '\"') + '"'
        }
        else {
            $_
        }
    })
    return '"' + $Executable + '" ' + ($escaped -join ' ')
}

function Invoke-Captured {
    param(
        [Parameter(Mandatory = $true)] [string] $Executable,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]] $Arguments,
        [Parameter(Mandatory = $true)] [string] $LogPath,
        [switch] $AllowFailure
    )

    $commandLine = Format-CommandLine $Executable $Arguments
    Add-Content -LiteralPath $commandLogPath -Value $commandLine -Encoding UTF8
    Write-Host $commandLine

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& $Executable @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    catch {
        $output = @($_ | Out-String)
        $exitCode = -1
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    $output | ForEach-Object { Write-Host $_ }
    $output | Out-File -LiteralPath $LogPath -Encoding utf8
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "Command failed with exit code $exitCode. See $LogPath"
    }

    return $exitCode
}

function Export-Json {
    param([Parameter(Mandatory = $true)] $Value, [Parameter(Mandatory = $true)] [string] $Path)
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Get-CimSafe {
    param([Parameter(Mandatory = $true)] [string] $ClassName)

    try {
        return @(Get-CimInstance -ClassName $ClassName -ErrorAction Stop)
    }
    catch {
        Add-Content -LiteralPath (Join-Path $logsDirectory "system-collection-errors.txt") `
            -Value ("{0}: {1}" -f $ClassName, ($_ | Out-String).Trim()) `
            -Encoding UTF8
        return @()
    }
}

function Get-PropertyValue {
    param($Object, [Parameter(Mandatory = $true)] [string] $Name)

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Find-VTune {
    if (-not [string]::IsNullOrWhiteSpace($VtunePath)) {
        $resolved = [System.IO.Path]::GetFullPath($VtunePath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "VTune executable was not found: $resolved"
        }
        return $resolved
    }

    $command = Get-Command vtune.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) {
        return $command.Source
    }

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $candidates += Join-Path ${env:ProgramFiles(x86)} "Intel/oneAPI/vtune/latest/bin64/vtune.exe"
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $candidates += Join-Path $env:ProgramFiles "Intel/oneAPI/vtune/latest/bin64/vtune.exe"
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    return $null
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$packageInfo = $null
if (Test-Path -LiteralPath $packageManifestPath -PathType Leaf) {
    $packageInfo = Get-Content -LiteralPath $packageManifestPath -Raw | ConvertFrom-Json
}
$packageCommit = if ($null -ne $packageInfo -and $null -ne $packageInfo.commit) { [string] $packageInfo.commit } else { "unknown" }

$computerSystem = @(Get-CimSafe "Win32_ComputerSystem" | Select-Object -First 1)
$computerSystem = if ($computerSystem.Count -eq 0) { $null } else { $computerSystem[0] }
$operatingSystem = @(Get-CimSafe "Win32_OperatingSystem" | Select-Object -First 1)
$operatingSystem = if ($operatingSystem.Count -eq 0) { $null } else { $operatingSystem[0] }
$processors = @(Get-CimSafe "Win32_Processor")
$bios = @(Get-CimSafe "Win32_BIOS" | Select-Object -First 1)
$bios = if ($bios.Count -eq 0) { $null } else { $bios[0] }
$memoryModules = @(Get-CimSafe "Win32_PhysicalMemory")
$metadata = [ordered]@{
    schemaVersion = 1
    capturedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    machineName = $env:COMPUTERNAME
    userInteractive = [Environment]::UserInteractive
    isAdministrator = $isAdministrator
    processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    powershellVersion = $PSVersionTable.PSVersion.ToString()
    packageCommit = $packageCommit
    workload = [ordered]@{
        inputSampleRateHz = 100000000
        channelCount = 100
        strategy = $Strategy
        simd = $Simd
        pfbDesign = $PfbDesign
        warmupIterations = $WarmupIterations
        iterations = $Iterations
    }
    selectedEnvironment = [ordered]@{
        ProcessorIdentifier = $env:PROCESSOR_IDENTIFIER
        DOTNET_TieredPGO = $env:DOTNET_TieredPGO
        DOTNET_ReadyToRun = $env:DOTNET_ReadyToRun
        DOTNET_TC_QuickJitForLoops = $env:DOTNET_TC_QuickJitForLoops
        COMPlus_TieredCompilation = $env:COMPlus_TieredCompilation
        COMPlus_ReadyToRun = $env:COMPlus_ReadyToRun
    }
}
Export-Json $metadata (Join-Path $captureDirectory "capture-metadata.json")

Export-Json ([ordered]@{
    manufacturer = Get-PropertyValue $computerSystem "Manufacturer"
    model = Get-PropertyValue $computerSystem "Model"
    totalPhysicalMemoryBytes = Get-PropertyValue $computerSystem "TotalPhysicalMemory"
    hypervisorPresent = Get-PropertyValue $computerSystem "HypervisorPresent"
}) (Join-Path $captureDirectory "computer-system.json")

Export-Json ([ordered]@{
    caption = Get-PropertyValue $operatingSystem "Caption"
    version = Get-PropertyValue $operatingSystem "Version"
    buildNumber = Get-PropertyValue $operatingSystem "BuildNumber"
    osArchitecture = Get-PropertyValue $operatingSystem "OSArchitecture"
    lastBootUpTime = Get-PropertyValue $operatingSystem "LastBootUpTime"
    freePhysicalMemoryKiB = Get-PropertyValue $operatingSystem "FreePhysicalMemory"
    totalVisibleMemorySizeKiB = Get-PropertyValue $operatingSystem "TotalVisibleMemorySize"
}) (Join-Path $captureDirectory "operating-system.json")

Export-Json @($processors | ForEach-Object {
    [ordered]@{
        name = Get-PropertyValue $_ "Name"
        manufacturer = Get-PropertyValue $_ "Manufacturer"
        description = Get-PropertyValue $_ "Description"
        numberOfCores = Get-PropertyValue $_ "NumberOfCores"
        numberOfLogicalProcessors = Get-PropertyValue $_ "NumberOfLogicalProcessors"
        maxClockSpeedMHz = Get-PropertyValue $_ "MaxClockSpeed"
        currentClockSpeedMHz = Get-PropertyValue $_ "CurrentClockSpeed"
        virtualizationFirmwareEnabled = Get-PropertyValue $_ "VirtualizationFirmwareEnabled"
        secondLevelAddressTranslationExtensions = Get-PropertyValue $_ "SecondLevelAddressTranslationExtensions"
    }
}) (Join-Path $captureDirectory "processors.json")

Export-Json ([ordered]@{
    manufacturer = Get-PropertyValue $bios "Manufacturer"
    name = Get-PropertyValue $bios "Name"
    smbiosBiosVersion = Get-PropertyValue $bios "SMBIOSBIOSVersion"
    releaseDate = Get-PropertyValue $bios "ReleaseDate"
}) (Join-Path $captureDirectory "bios.json")

Export-Json @($memoryModules | ForEach-Object {
    [ordered]@{
        manufacturer = Get-PropertyValue $_ "Manufacturer"
        partNumber = ([string] (Get-PropertyValue $_ "PartNumber")).Trim()
        capacityBytes = Get-PropertyValue $_ "Capacity"
        speedMTs = Get-PropertyValue $_ "Speed"
        configuredClockSpeedMTs = Get-PropertyValue $_ "ConfiguredClockSpeed"
    }
}) (Join-Path $captureDirectory "memory-modules.json")

Get-ChildItem -LiteralPath (Join-Path $packageRoot "app") -File |
    Where-Object { $_.Extension -in ".exe", ".dll", ".pdb", ".json" } |
    ForEach-Object {
        [ordered]@{
            file = $_.Name
            length = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $captureDirectory "application-files.json") -Encoding UTF8

Invoke-Captured "systeminfo.exe" @() (Join-Path $logsDirectory "systeminfo.txt") -AllowFailure | Out-Null
Invoke-Captured "powercfg.exe" @("/getactivescheme") (Join-Path $logsDirectory "active-power-plan.txt") -AllowFailure | Out-Null
Invoke-Captured "bcdedit.exe" @("/enum") (Join-Path $logsDirectory "bcdedit.txt") -AllowFailure | Out-Null
foreach ($service in "sepdrv", "socperf3", "vtss") {
    Invoke-Captured "sc.exe" @("query", $service) (Join-Path $logsDirectory "service-$service.txt") -AllowFailure | Out-Null
}

$vtuneExecutable = Find-VTune
if ($null -ne $vtuneExecutable) {
    Invoke-Captured $vtuneExecutable @("-version") (Join-Path $logsDirectory "vtune-version.txt") -AllowFailure | Out-Null
    Invoke-Captured $vtuneExecutable @("-help", "collect", "hotspots") (Join-Path $logsDirectory "vtune-help-hotspots.txt") -AllowFailure | Out-Null
}
elseif (-not $SystemInfoOnly) {
    throw "vtune.exe was not found. Install Intel VTune Profiler, initialize its environment, or pass -VtunePath."
}

function New-WorkloadArguments {
    param([Parameter(Mandatory = $true)] [string] $OutputPath)

    return @(
        "--optimization-profile",
        "--strategy", $Strategy,
        "--simd", $Simd,
        "--pfb-design", $PfbDesign,
        "--warmup", $WarmupIterations.ToString([Globalization.CultureInfo]::InvariantCulture),
        "--iterations", $Iterations.ToString([Globalization.CultureInfo]::InvariantCulture),
        "--commit", $packageCommit,
        "--output", $OutputPath
    )
}

if (-not $SystemInfoOnly) {
    $baselineArguments = New-WorkloadArguments (Join-Path $captureDirectory "baseline-workload.json")
    Invoke-Captured $applicationPath $baselineArguments (Join-Path $logsDirectory "baseline-workload.txt") | Out-Null

    $hotspotsResult = Join-Path $captureDirectory "result-hotspots"
    $hotspotsWorkloadArguments = New-WorkloadArguments (Join-Path $captureDirectory "hotspots-workload.json")
    $hotspotsArguments = @(
        "-collect", "hotspots",
        "-knob", "sampling-mode=$HotspotsSamplingMode",
        "-knob", "enable-stack-collection=true",
        "-knob", "enable-characterization-insights=false",
        "-mrte-mode", "mixed",
        "-result-dir", $hotspotsResult,
        "--", $applicationPath
    ) + $hotspotsWorkloadArguments
    Invoke-Captured $vtuneExecutable $hotspotsArguments (Join-Path $logsDirectory "vtune-collect-hotspots.txt") | Out-Null

    Invoke-Captured $vtuneExecutable @(
        "-report", "summary", "-r", $hotspotsResult,
        "-format", "html", "-report-output", (Join-Path $reportsDirectory "hotspots-summary.html")
    ) (Join-Path $logsDirectory "vtune-report-hotspots-summary.txt") -AllowFailure | Out-Null
    Invoke-Captured $vtuneExecutable @(
        "-report", "hotspots", "-r", $hotspotsResult,
        "-group-by", "function", "-format", "csv", "-csv-delimiter", "comma",
        "-report-output", (Join-Path $reportsDirectory "hotspots-by-function.csv")
    ) (Join-Path $logsDirectory "vtune-report-hotspots-functions.txt") -AllowFailure | Out-Null

    if ($IncludeMicroarchitecture) {
        $uarchResult = Join-Path $captureDirectory "result-uarch-exploration"
        $uarchWorkloadArguments = New-WorkloadArguments (Join-Path $captureDirectory "uarch-workload.json")
        $uarchArguments = @(
            "-collect", "uarch-exploration",
            "-mrte-mode", "mixed",
            "-result-dir", $uarchResult,
            "--", $applicationPath
        ) + $uarchWorkloadArguments
        Invoke-Captured $vtuneExecutable $uarchArguments (Join-Path $logsDirectory "vtune-collect-uarch-exploration.txt") | Out-Null

        Invoke-Captured $vtuneExecutable @(
            "-report", "summary", "-r", $uarchResult,
            "-format", "html", "-report-output", (Join-Path $reportsDirectory "uarch-summary.html")
        ) (Join-Path $logsDirectory "vtune-report-uarch-summary.txt") -AllowFailure | Out-Null
        Invoke-Captured $vtuneExecutable @(
            "-report", "hotspots", "-r", $uarchResult,
            "-group-by", "function", "-format", "csv", "-csv-delimiter", "comma",
            "-report-output", (Join-Path $reportsDirectory "uarch-hotspots-by-function.csv")
        ) (Join-Path $logsDirectory "vtune-report-uarch-hotspots.txt") -AllowFailure | Out-Null
        Invoke-Captured $vtuneExecutable @(
            "-report", "hw-events", "-r", $uarchResult,
            "-format", "csv", "-csv-delimiter", "comma",
            "-report-output", (Join-Path $reportsDirectory "uarch-hw-events.csv")
        ) (Join-Path $logsDirectory "vtune-report-uarch-hw-events.txt") -AllowFailure | Out-Null
    }
}

$archivePath = "$captureDirectory.zip"
Compress-Archive -LiteralPath $captureDirectory -DestinationPath $archivePath -CompressionLevel Optimal
Write-Host "Capture directory: $captureDirectory"
Write-Host "Send this archive back for analysis: $archivePath"
Write-Output $archivePath
