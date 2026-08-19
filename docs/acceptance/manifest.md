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
| FFTW runtime identity, exports, alignment, cache and steady-state allocation contract | Runtime owner | `FftwTests` plus pinned hashes in `docs/fftw-runtime.md` | Enforced |
| Scalar primitive, FFTW, planning, FDC and PFB benchmark entry points exist | Performance owner | `dotnet run --project benchmarks/IqChannelizer.Benchmarks -c Release -- --list flat` | Enforced; no performance claim |
| SigMF generator model/DSP/export behavior | Tooling owner | `pnpm test` and `pnpm run build` in `tools/sigmf-generator` | Enforced |
| Redistributable package is license-approved | Release owner | `docs/release-policy.md`; library has `IsPackable=false` | Blocked by policy decision |
| SIMD dispatch and optimized kernels | Performance owner | No accepted fixture yet | Deferred until explicit approval |
| `Auto`, FoldAware prototype and selected-bin PFB | Architecture owner | No accepted benchmark/signal profile yet | Deliberately unsupported |

## Reproducible verification

Run from the repository root:

```powershell
dotnet restore IqChannelizer.sln
dotnet test IqChannelizer.sln -c Release --no-restore
dotnet format IqChannelizer.sln --verify-no-changes --no-restore
dotnet run --project benchmarks/IqChannelizer.Benchmarks -c Release --no-build -- --list flat
pnpm --dir tools/sigmf-generator test
pnpm --dir tools/sigmf-generator run build
```

The exact passing test count and verified working-tree revision are recorded in
[`implementation-steps.md`](../../implementation-steps.md) after each implementation
increment.
