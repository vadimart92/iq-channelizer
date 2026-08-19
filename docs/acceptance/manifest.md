# Acceptance manifest

This manifest maps each currently enforced release invariant to its automated fixture
and owner role. It is the tracked source of truth; generated test and benchmark output
remains under ignored `TestResults/` and `BenchmarkDotNet.Artifacts/` directories.

| Gate | Owner role | Automated evidence | Current state |
| --- | --- | --- | --- |
| Public request/plan validation, exact counts and timing | Core API owner | `ContractTests`, `StreamingFlowTests` | Enforced |
| Canonical signed-bin mapping, including exact `-Fs/2` | DSP owner | `ContractTests.NegativeNyquistUsesTheCanonicalSignedBin` | Enforced |
| Failed output sink cannot leave a silently reusable partial state | Streaming owner | `StreamingFlowTests.FailedSinkFaultsEngineUntilReset` for FDC and PFB | Enforced |
| FDC overlap-save output matches independent double-precision DDC | DSP owner | `FdcOverlapSaveTests`, `FdcPlannerTests` | Enforced |
| Every folded alias image is exercised for representative FDC and PFB plans | DSP owner | `SignalValidationAcceptanceTests`; compact result in `artifacts/signal-validation/scalar-acceptance.json` | Enforced |
| Worst-case PFB residual at both `-DeltaF/2` and `+DeltaF/2` preserves amplitude | DSP owner | `SignalValidationAcceptanceTests.PfbAcceptsBothWorstCaseHalfBinResiduals` | Enforced |
| Generalized PFB algebra and absolute frame correction | DSP owner | `PfbAlgebraTests`, `PfbMathTests` | Enforced |
| Automatic PFB planning can select `H != K` and `H != K/2` | Planner owner | `PfbPlannerTests.AutomaticPlannerSelectsFeasibleArbitraryHopAndExactChunkShape` | Enforced |
| `FramesPerBatch` does not change the logical PFB stream | Streaming owner | `PfbProductionFlowTests.FramesPerBatchDoesNotChangeLogicalOutput` | Enforced |
| Per-channel PFB filtering remains active for `Dfine=1` | DSP owner | `PfbProductionFlowTests.FineFactorOneStillAppliesTheRequestedPerChannelFilter` | Enforced |
| Filter pass/fail checks cannot miss a narrow between-grid peak | DSP owner | adversarial tests in `FilterDesignTests` | Enforced |
| Absolute oscillator phase remains stable at long stream indices | DSP owner | large-origin cases in `StreamingFlowTests` and `PfbMathTests` | Enforced |
| FFTW runtime identity, exports, alignment, cache and steady-state allocation contract | Runtime owner | `FftwTests` plus pinned hashes in `docs/fftw-runtime.md` | Enforced |
| Diagnostics counters, stage timing, fault/reset status and enabled/disabled allocation contract | Runtime owner | `DiagnosticsTests` | Enforced |
| Unified facade plan snapshots, lifecycle and reconfiguration boundary | Core API owner | `ContractTests.ResolvedPlanCollectionsAreImmutableSnapshots`, `docs/facade.md` | Enforced |
| Scalar/AVX2/AVX-512 primitive, FFTW, planning, FDC and PFB statistical evidence exists | Performance owner | `artifacts/benchmarks/latest-summary.md`, retained BDN CSV/Markdown, and schema-v2 `stage-profile.json` | Enforced for recorded configurations; no general realtime claim |
| Managed-only NuGet excludes FFTW native assets and runs in a clean consumer | Release owner | `build/verify-package.ps1`, `artifacts/package-validation.json`; native assets have `Pack=false` | Enforced |
| SIMD dispatch, scalar fallback and unsupported-ISA behavior | Performance owner | `ContractTests`, `SimdEngineTests`, `SimdTests` | Enforced for Scalar, AVX2/FMA and AVX-512F |
| AVX2/AVX-512 PFB direct rotated-store FIR | DSP/performance owner | `PfbSimdTests`, `PfbAlgebraTests`, `PfbProductionFlowTests`, retained `PfbFirBenchmarks` reports | Enforced |
| AVX2/AVX-512 FDC wrapped complex extraction | DSP/performance owner | `SimdTests`, `FdcOverlapSaveTests`, `SimdEngineTests`, retained `FdcExtractionBenchmarks` reports | Enforced |
| Explicit FoldAware PFB prototype | DSP/performance owner | `PfbFoldAwareTests`, dual-mode `SignalValidationAcceptanceTests`, schema-v2 signal summary and retained `PfbPrototypeBenchmarks` reports | Enforced as explicit opt-in; Conservative remains default |
| Selected-bin/direct-DFT PFB | Architecture/performance owner | `PfbSelectedBinDftTests`, retained `PfbSelectedBinBenchmarks` reports and schema-v2 stage profile | Guarded: internal prototype is not wired because measured crossover is shape-limited and FFTW is not dominant |
| Channelizer strategy `Auto` | Architecture/performance owner | `StrategyProfileTests`, `artifacts/benchmarks/strategy-profile-v1.json`, retained equal-spec engine report | Enforced for exact profile matches; unmatched environments/shapes are guarded |

## Reproducible verification

Run from the repository root:

```powershell
dotnet restore IqChannelizer.sln
dotnet test IqChannelizer.sln -c Release --no-restore --results-directory TestResults
dotnet format IqChannelizer.sln --verify-no-changes --no-restore
dotnet run --project benchmarks/IqChannelizer.Benchmarks -c Release --no-build -- --list flat
```

`tools/sigmf-generator` is a separately owned project and is intentionally outside
the IqChannelizer acceptance, release, and Definition-of-Done scope.

The exact passing test count and verified working-tree revision are recorded in
[`implementation-steps.md`](../../implementation-steps.md) after each implementation
increment. The current section-14 Definition-of-Done audit, including deferred and blocked
gates, is in [`report.md`](report.md).
