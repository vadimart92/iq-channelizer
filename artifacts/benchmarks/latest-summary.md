# Benchmark baseline summary

## Identity

- Measured source state: `1d4fc31+step13-working-tree`.
- Generated: 2026-08-19.
- BenchmarkDotNet: 0.15.8; end-to-end engine comparison uses the default statistical job.
- CPU: AMD Ryzen 5 8500G, 6 physical / 12 logical cores, high-performance power plan during BDN runs.
- OS/runtime: Windows 11 10.0.26200.9168, .NET 10.0.11 x64 RyuJIT x86-64-v4.
- FFTW: `fftw-3.3.5-sse2-avx`, single precision, one thread.

Decision-grade engine command:

```powershell
dotnet run --project benchmarks/IqChannelizer.Benchmarks -c Release --no-build -- `
  --filter "*EngineBenchmarks*" --artifacts BenchmarkDotNet.Artifacts/step13-engine-final
```

Kernel evidence used short statistical jobs to make the production-wiring decision; the retained
reports record their exact job metadata. The schema-v2 stage profile uses 2,048 same-engine
stabilization calls followed by 5,000 measured calls.

## Statistical end-to-end results

Means are normalized per input complex sample. All 12 cases report zero managed allocation.

| Strategy | Channels | Scalar ns/sample | AVX2 ns/sample | Scalar MS/s | AVX2 MS/s |
| --- | ---: | ---: | ---: | ---: | ---: |
| FDC | 1 | 2.700 | 2.673 | 370.4 | 374.1 |
| FDC | 8 | 4.153 | 4.018 | 240.8 | 248.9 |
| FDC | 32 | 9.079 | 8.417 | 110.1 | 118.8 |
| PFB | 1 | 22.541 | 10.113 | 44.4 | 98.9 |
| PFB | 8 | 25.084 | 13.053 | 39.9 | 76.6 |
| PFB | 32 | 36.806 | 24.410 | 27.2 | 41.0 |

AVX2 improves every recorded BDN engine case. The FDC benefit is modest and grows with channel
count; the PFB direct-store FIR is the material optimization. These results describe the exact
recorded host/configurations and are not a portable realtime claim.

## Kernel decisions

- PFB FIR, 8 taps/phase: scalar 8.982, compact AVX2 5.925, expanded AVX2 3.676 ns/input sample; 20 taps/phase: 24.371, 14.088 and 8.594 respectively. Production therefore keeps only the selected expanded layout.
- FDC complex extraction after equal validation policy, 128 bins: 124.87 -> 37.09 ns; 512 bins: 507.41 -> 122.89 ns.
- Residual rotator: 1.886 -> 1.853 ns/sample in the short run. The delta was not decision-grade, so production retains the scalar rotator.
- AVX-512 remains disabled: it has no accepted comparative implementation/profile against AVX2.

## Integration stage profile

The representative profile uses eight channels, 4096 input samples per block, 5,000 measured
blocks, one FFTW thread and `StageTiming`. All four loops report zero managed allocation.

| Strategy | Backend | ns/input sample | MS/s | p50 block | p99 block |
| --- | --- | ---: | ---: | ---: | ---: |
| FDC | Scalar | 4.34 | 230.3 | 0.02 ms | 0.03 ms |
| FDC | AVX2 | 5.40 | 185.1 | 0.02 ms | 0.03 ms |
| PFB | Scalar | 26.50 | 37.7 | 0.11 ms | 0.17 ms |
| PFB | AVX2 | 13.49 | 74.1 | 0.05 ms | 0.08 ms |

StageTiming changes the measured shape and, for FDC, does not reproduce the small diagnostics-off
BDN advantage. Backend acceptance therefore uses the full BDN comparison plus correctness data,
not this single instrumented profile in isolation. The complete counters, latency distribution,
working set and stage ticks are in [stage-profile.json](stage-profile.json).

## Retained reports

- [Engine Markdown](results/IqChannelizer.Benchmarks.EngineBenchmarks-report-github.md) / [CSV](results/IqChannelizer.Benchmarks.EngineBenchmarks-report.csv)
- [PFB FIR Markdown](results/IqChannelizer.Benchmarks.PfbFirBenchmarks-report-github.md) / [CSV](results/IqChannelizer.Benchmarks.PfbFirBenchmarks-report.csv)
- [FDC extraction Markdown](results/IqChannelizer.Benchmarks.FdcExtractionBenchmarks-report-github.md) / [CSV](results/IqChannelizer.Benchmarks.FdcExtractionBenchmarks-report.csv)
- [Residual rotator Markdown](results/IqChannelizer.Benchmarks.ResidualRotatorBenchmarks-report-github.md) / [CSV](results/IqChannelizer.Benchmarks.ResidualRotatorBenchmarks-report.csv)
- Existing FFTW, planning and scalar FIR reports remain retained beside these files.
