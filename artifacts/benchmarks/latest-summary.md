# Benchmark baseline summary

## Identity

- Measured source state: `998d427+avx512-working-tree`.
- Generated: 2026-08-19.
- BenchmarkDotNet: 0.15.8; end-to-end engine comparison uses the default statistical job.
- CPU: AMD Ryzen 5 8500G, 6 physical / 12 logical cores, high-performance power plan during BDN runs.
- OS/runtime: Windows 11 10.0.26200.9168, .NET 10.0.11 x64 RyuJIT x86-64-v4.
- FFTW: `fftw-3.3.5-sse2-avx`, single precision, one thread.

Decision-grade engine command:

```powershell
dotnet run --project benchmarks/IqChannelizer.Benchmarks -c Release --no-build -- `
  --filter "*EngineBenchmarks*" --artifacts BenchmarkDotNet.Artifacts/avx512-engine-statistical
```

Kernel evidence used short statistical jobs to make the production-wiring decision; the retained
reports record their exact job metadata. The schema-v2 stage profile uses 8,192 same-engine
stabilization calls followed by 5,000 measured calls.

## Statistical end-to-end results

Means are normalized per input complex sample. All 18 cases report zero managed allocation.

| Strategy | Channels | Scalar ns/sample | AVX2 ns/sample | AVX-512 ns/sample |
| --- | ---: | ---: | ---: | ---: |
| FDC | 1 | 2.691 | 2.682 | 2.724 |
| FDC | 8 | 4.450 | 4.496 | 4.474 |
| FDC | 32 | 10.410 | 9.315 | 9.373 |
| PFB | 1 | 21.943 | 9.953 | 9.138 |
| PFB | 8 | 25.659 | 12.758 | 11.625 |
| PFB | 32 | 39.122 | 24.382 | 23.952 |

AVX-512 improves every recorded PFB shape over AVX2, so PFB `Auto` prefers AVX-512F. FDC is mixed:
AVX2 wins the 1/32-channel shapes while AVX-512 is marginally ahead at 8 channels, so FDC `Auto`
retains AVX2 and AVX-512 remains force-selectable. These results describe the exact recorded
host/configurations and are not a portable realtime claim.

## Kernel decisions

- PFB FIR, 8 taps/phase: scalar 8.991, expanded AVX2 3.658, expanded AVX-512 3.242 ns/input sample; 20 taps/phase: 21.169, 8.171 and 6.734 respectively.
- FDC complex extraction after equal validation policy, 128 bins: scalar 124.68, AVX2 35.10, AVX-512 33.71 ns; 512 bins: 503.92, 122.41 and 102.05 ns.
- Residual rotator: 1.886 -> 1.853 ns/sample in the short run. The delta was not decision-grade, so production retains the scalar rotator.
- AVX-512F is accepted for both forced backends and selected automatically for PFB on supporting hosts.

## Integration stage profile

The representative profile uses eight channels, 4096 input samples per block, 5,000 measured
blocks, one FFTW thread and `StageTiming`. All six loops report zero managed allocation.

| Strategy | Backend | ns/input sample | MS/s | p50 block | p99 block |
| --- | --- | ---: | ---: | ---: | ---: |
| FDC | Scalar | 4.41 | 226.9 | 0.02 ms | 0.03 ms |
| FDC | AVX2 | 4.20 | 237.9 | 0.02 ms | 0.03 ms |
| FDC | AVX-512 | 4.17 | 239.8 | 0.02 ms | 0.02 ms |
| PFB | Scalar | 26.85 | 37.2 | 0.11 ms | 0.16 ms |
| PFB | AVX2 | 13.56 | 73.8 | 0.05 ms | 0.09 ms |
| PFB | AVX-512 | 12.45 | 80.3 | 0.05 ms | 0.08 ms |

StageTiming changes the measured shape and, for FDC, does not reproduce the small diagnostics-off
BDN advantage. Backend acceptance therefore uses the full BDN comparison plus correctness data,
not this single instrumented profile in isolation. The complete counters, latency distribution,
working set and stage ticks are in [stage-profile.json](stage-profile.json).

## Step 14 experiments

FoldAware is an explicit PFB prototype-design hint; deterministic planning still defaults to
Conservative until a versioned planner profile exists. On the recorded eight-channel `K=64`,
`H=32`, AVX-512 shape, FoldAware reduced end-to-end processing from 12.281 to 7.232 ns/input
sample (41.1%) with zero managed allocation. Both modes pass the folded evaluator, passband test,
and all 15 final-rate blocker images.

The selected-bin direct-DFT prototype is correctness-tested but remains unwired. It beats
FFTW+gather for `Q=1` at `K=64/512` and for `K=512,Q=4`, but loses at `K=64,Q>=4` and
`K=512,Q=8`. Moreover, FFTW accounts for only about 9% of the current representative AVX-512
PFB stage profile. This does not justify another production path or an `Auto` rule yet.

## Retained reports

- [Engine Markdown](results/IqChannelizer.Benchmarks.EngineBenchmarks-report-github.md) / [CSV](results/IqChannelizer.Benchmarks.EngineBenchmarks-report.csv)
- [PFB FIR Markdown](results/IqChannelizer.Benchmarks.PfbFirBenchmarks-report-github.md) / [CSV](results/IqChannelizer.Benchmarks.PfbFirBenchmarks-report.csv)
- [FDC extraction Markdown](results/IqChannelizer.Benchmarks.FdcExtractionBenchmarks-report-github.md) / [CSV](results/IqChannelizer.Benchmarks.FdcExtractionBenchmarks-report.csv)
- [Residual rotator Markdown](results/IqChannelizer.Benchmarks.ResidualRotatorBenchmarks-report-github.md) / [CSV](results/IqChannelizer.Benchmarks.ResidualRotatorBenchmarks-report.csv)
- [AVX-512 engine Markdown](results/IqChannelizer.Benchmarks.EngineBenchmarks-Avx512-report-github.md) / [CSV](results/IqChannelizer.Benchmarks.EngineBenchmarks-Avx512-report.csv)
- [AVX-512 PFB FIR Markdown](results/IqChannelizer.Benchmarks.PfbFirBenchmarks-Avx512-report-github.md) / [CSV](results/IqChannelizer.Benchmarks.PfbFirBenchmarks-Avx512-report.csv)
- [AVX-512 FDC extraction Markdown](results/IqChannelizer.Benchmarks.FdcExtractionBenchmarks-Avx512-report-github.md) / [CSV](results/IqChannelizer.Benchmarks.FdcExtractionBenchmarks-Avx512-report.csv)
- [FoldAware comparison Markdown](results/IqChannelizer.Benchmarks.PfbPrototypeBenchmarks-report-github.md) / [CSV](results/IqChannelizer.Benchmarks.PfbPrototypeBenchmarks-report.csv)
- [Selected-bin crossover Markdown](results/IqChannelizer.Benchmarks.PfbSelectedBinBenchmarks-report-github.md) / [CSV](results/IqChannelizer.Benchmarks.PfbSelectedBinBenchmarks-report.csv)
- Existing FFTW, planning and scalar FIR reports remain retained beside these files.
