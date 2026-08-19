# Benchmarks

The existing benchmark project uses BenchmarkDotNet 0.15.8 and contains these families:

- `PrimitiveBenchmarks`: scalar FIR work, normalized per output sample;
- `FftwBenchmarks`: single-precision forward transform execution;
- `PfbFirBenchmarks`: scalar versus AVX2 and AVX-512 phase-parallel direct-store FIR kernels;
- `PfbPrototypeBenchmarks`: Conservative versus explicit FoldAware end-to-end PFB processing;
- `PfbSelectedBinBenchmarks`: FFTW+gather versus scalar/AVX2/AVX-512 direct-DFT crossover;
- `FdcExtractionBenchmarks`: scalar versus AVX2 and AVX-512 complex-window extraction;
- `ResidualRotatorBenchmarks`: measured scalar versus AVX2 residual rotation;
- `EngineBenchmarks`: steady-state scalar, AVX2 and AVX-512 FDC/PFB processing for 1, 8, and 32 channels,
  normalized per 4096-sample input chunk;
- `PlanningBenchmarks`: engine/filter/buffer initialization with a warm native-plan cache.

List or run the suites from the repository root:

```powershell
dotnet run --project benchmarks/IqChannelizer.Benchmarks -c Release -- --list flat
dotnet run --project benchmarks/IqChannelizer.Benchmarks -c Release
```

Use `--job Dry --filter "*"` only to prove that every generated harness builds and runs.
Dry measurements have one sample and are not performance evidence. A decision-grade run
must retain the generated Markdown/CSV output together with the commit, CPU, OS, runtime,
FFTW version, power policy, and exact command. Only such a stored comparative profile may
feed a future strategy `Auto`, automatic FoldAware selection, selected-bin production path,
ISA, or realtime decision. The
accepted AVX2/AVX-512 dispatch evidence is retained under `artifacts/benchmarks/results/` and summarized
in `artifacts/benchmarks/latest-summary.md`.

The only accepted channelizer-strategy profile is
`artifacts/benchmarks/strategy-profile-v1.json`. Runtime `Auto` requires an exact match to
its environment and request-family fields; the profile is not a general cost model.

For an allocation and latency integration profile with diagnostics-based stage totals:

```powershell
dotnet run --project benchmarks/IqChannelizer.Benchmarks -c Release --no-build -- `
  --stage-profile --commit <git-sha> `
  --output artifacts/benchmarks/stage-profile.json
```

The stage profile performs 8,192 same-engine stabilization calls to move tiered-JIT work outside
the measurement window, then uses preallocated latency storage and measures 2,000 calls by default.
It reports p50/p95/p99/max latency, managed allocation delta, resolved working-set estimate,
process working-set snapshots, throughput and engine-specific stage ticks for scalar and
available AVX2/AVX-512 backends. These figures are machine-specific evidence, not portable performance
guarantees.
