# Benchmarks

The existing benchmark project uses BenchmarkDotNet 0.15.8 and contains four families:

- `PrimitiveBenchmarks`: scalar FIR work, normalized per output sample;
- `FftwBenchmarks`: single-precision forward transform execution;
- `EngineBenchmarks`: steady-state FDC and PFB processing for 1, 8, and 32 channels,
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
feed a future `Auto`, SIMD, FoldAware, selected-bin, or realtime decision.
