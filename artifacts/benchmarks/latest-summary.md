# Benchmark baseline summary

## Identity

- Measured source state: `db4e98d+step12-working-tree`; the benchmark harness and retained evidence are committed together after measurement.
- Generated: 2026-08-19.
- BenchmarkDotNet: 0.15.8, default statistical job.
- CPU: AMD Ryzen 5 8500G, 6 physical / 12 logical cores, high-performance power plan during BDN runs.
- OS: Windows 11 10.0.26200.9168.
- Runtime: .NET 10.0.11, x64 RyuJIT x86-64-v4; SDK 10.0.303.
- FFTW: `fftw-3.3.5-sse2-avx`, single precision, one thread.

Commands:

```powershell
dotnet run --project benchmarks/IqChannelizer.Benchmarks -c Release --no-build -- `
  --artifacts BenchmarkDotNet.Artifacts/step12-db4e98d --filter "*"
dotnet run --project benchmarks/IqChannelizer.Benchmarks -c Release --no-build -- `
  --stage-profile --commit db4e98d+step12-working-tree `
  --output artifacts/benchmarks/stage-profile.json
```

## Statistical results

Steady-state engine means are normalized per input complex sample (`OperationsPerInvoke=4096`).
No managed allocations were detected.

| Strategy | Channels | Mean ns/input sample | Approx. MS/s |
| --- | ---: | ---: | ---: |
| FDC | 1 | 2.710 | 369.0 |
| FDC | 8 | 4.590 | 217.9 |
| FDC | 32 | 10.900 | 91.7 |
| PFB | 1 | 31.755 | 31.5 |
| PFB | 8 | 35.339 | 28.3 |
| PFB | 32 | 47.711 | 21.0 |

Additional baselines:

- FFTW forward 1024: 738.2 ns; forward 4096: 4.876 us; no managed allocation.
- Scalar FIR: 16.21 ns/output for 31 taps and 73.23 ns/output for 127 taps; no managed allocation.
- Warm-cache creation: FDC 2.466 ms / 571.42 KB allocated; PFB 1.395 ms / 1102.56 KB allocated.

Raw reports:

- [engine Markdown](results/IqChannelizer.Benchmarks.EngineBenchmarks-report-github.md) / [CSV](results/IqChannelizer.Benchmarks.EngineBenchmarks-report.csv)
- [FFTW Markdown](results/IqChannelizer.Benchmarks.FftwBenchmarks-report-github.md) / [CSV](results/IqChannelizer.Benchmarks.FftwBenchmarks-report.csv)
- [planning Markdown](results/IqChannelizer.Benchmarks.PlanningBenchmarks-report-github.md) / [CSV](results/IqChannelizer.Benchmarks.PlanningBenchmarks-report.csv)
- [primitive Markdown](results/IqChannelizer.Benchmarks.PrimitiveBenchmarks-report-github.md) / [CSV](results/IqChannelizer.Benchmarks.PrimitiveBenchmarks-report.csv)

## Integration stage profile

The representative profile uses 1 MHz metadata, eight channels, 4096 input samples per block,
2,000 measured blocks, scalar DSP, one FFTW thread and `StageTiming`. It stabilizes the same
engine once before measuring and reports zero managed bytes allocated in both processing loops.

| Metric | FDC | PFB |
| --- | ---: | ---: |
| Resolved shape | `N=5280,D=32` | `K=64,H=32,F=128` |
| Estimated engine working set | 187,712 B | 294,784 B |
| ns/input sample including timing | 16.42 | 50.82 |
| p50 block latency | 0.0633 ms | 0.1442 ms |
| p95 block latency | 0.0731 ms | 0.5653 ms |
| p99 block latency | 0.0880 ms | 0.5851 ms |
| maximum observed latency | 5.4211 ms | 4.0428 ms |
| input copy/polyphase share | 1.0% | 81.5% |
| FFTW share | 20.4% | 2.5% |
| channel-processing share | 77.2% | 15.8% |

The complete machine-readable profile, including process working-set snapshots and raw stage
ticks, is in [stage-profile.json](stage-profile.json). StageTiming intentionally adds clock-read
overhead and is not directly comparable to the diagnostics-disabled BDN means.

These measurements describe this exact scalar configuration on this machine. They are not a
general realtime claim, do not authorize SIMD/FoldAware/selected-bin wiring, and are not yet an
`Auto` strategy profile.
