# IqChannelizer VTune profile target

This package contains a self-contained Windows x64 build. A separate .NET installation is not required.

The built-in workload processes 100 channels from a synthetic 100 MHz input stream. It performs no input or output file I/O during the measured loop.

## Requirements

- Windows x64.
- Intel VTune Profiler installed.
- An elevated PowerShell session is recommended for hardware event-based analyses.
- Extract the entire archive before running it.

## Collect the default Hotspots profile

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Collect-VTune.ps1
```

The default uses VTune user-mode sampling (`sw`) and mixed managed/native symbolization. It also runs an unprofiled baseline and collects CPU, memory, BIOS, OS, hypervisor, power-plan, VTune version, driver status, application hashes, command lines, logs, HTML summaries and CSV reports.

## Add Microarchitecture Exploration

Run this on a supported Intel processor with the VTune sampling driver available:

```powershell
.\Collect-VTune.ps1 -HotspotsSamplingMode hw -IncludeMicroarchitecture
```

## Useful overrides

```powershell
.\Collect-VTune.ps1 -Simd avx2 -Iterations 8000
.\Collect-VTune.ps1 -VtunePath "C:\Program Files (x86)\Intel\oneAPI\vtune\latest\bin64\vtune.exe"
.\Collect-VTune.ps1 -SystemInfoOnly
```

If AVX-512 is unsupported on the target, keep the default `-Simd auto`.

When collection completes, the script prints the path of a `vtune-capture-*.zip` archive. Send that archive back for analysis. Keep the raw `result-*` directories in the archive; the VTune GUI can open them directly.

## Run only the workload

```powershell
.\app\IqChannelizer.Benchmarks.exe --optimization-profile --strategy pfb --simd auto --pfb-design conservative --warmup 256 --iterations 5000 --output baseline.json
```

Expected console output ends with the sustained input rate in MS/s and the fraction of the 100 MS/s target.
