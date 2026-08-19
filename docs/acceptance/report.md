# Definition-of-Done acceptance report

This report audits section 14 of [`implementation-plan.md`](../../implementation-plan.md)
against the Conservative/explicit FoldAware scalar/AVX2/AVX-512 milestone at commit `6aaa8ae`
plus the current profile-backed Auto changeset. It records capability status; it is not a redistributable-release approval
or a portable realtime claim.

Verification date: **2026-08-19**. Release suite: **235 passed, 0 failed, 0 skipped**.

Status meanings:

- **Enforced** — implemented and covered by automated or directly inspectable evidence;
- **Guarded** — the unsafe/unproven behavior is explicitly unavailable;
- **Deferred** — requires the permission or measurement gate named in the plan;
- **Blocked** — requires an external owner decision.

| Definition-of-Done requirement | Status | Evidence / remaining gate |
| --- | --- | --- |
| FDC and generalized PFB share one API | Enforced | `ChannelizerFactory`, `IStreamingChannelizer`, `ContractTests` |
| Public output-rate semantics are not power-of-two-only | Enforced | `ChannelRequest` rate requirements and generalized PFB planner tests |
| FDC may select power-of-two `D` | Enforced | `FdcPlannerTests` |
| PFB exposes `K` and `H` separately | Enforced | `ResolvedChannelizerPlan`, `ResolvedChannelPlan`, `ContractTests` |
| R=2 phase correction is explicit and proven | Enforced | `PfbMathTests`, `PfbAlgebraTests` |
| At least one `H != K` and `H != K/2` configuration passes | Enforced | planner-selected `H=33` signal test in `PfbPlannerTests` |
| PFB phase derives from the absolute stream and survives process boundaries | Enforced | `PfbAlgebraTests`, `PfbProductionFlowTests`, `StreamingFlowTests` |
| Absolute oscillator phase remains stable at large stream indices | Enforced | long-origin cases in `StreamingFlowTests` and `PfbMathTests` |
| Pre-FFT circular shift equals explicit post-FFT correction | Enforced | `PfbAlgebraTests` |
| SIMD PFB FIR stores directly into rotated FFTW input | Enforced | `PfbPhaseFir`, `PfbSimdTests`; at most two segments, no gather or intermediate K-vector |
| AVX2/FMA hot-kernel backend exists | Enforced | PFB FIR and FDC extraction kernels, scalar-vs-AVX2 engine fixtures and retained BDN reports |
| AVX-512 exists where worthwhile or has benchmark evidence against it | Enforced | AVX-512F PFB/FDC kernels, forced-backend equivalence, zero-allocation tests and retained AVX2 comparison reports |
| Scalar fallback exists | Enforced | Both production engines and scalar-vs-reference tests |
| ISA dispatch is outside inner loops | Enforced | `SimdBackendResolver` resolves once at creation; forced unsupported AVX2/AVX-512 requests produce actionable errors |
| FDC inverse normalization is explicit and amplitude-tested | Enforced | `FftwFdcEngine`, `FdcOverlapSaveTests` |
| Filter validation includes folded alias response | Enforced | filter tests and `SignalValidationAcceptanceTests` |
| Conservative and FoldAware designs can be compared safely | Enforced | explicit `PfbPrototypeDesignMode`, `PfbFoldAwareTests`, dual-mode 15-image blocker sweeps and retained end-to-end BDN comparison; Conservative remains default |
| Exact history/chunk contracts are enforced | Enforced | `StreamingFlowTests.ProcessEnforcesExactLengthAndContinuity` |
| Hot output API is only `Write(int, ReadOnlySpan<ComplexF>)` | Enforced | `IChannelOutputSink` |
| Channel IDs stay unique, opaque, and unremapped | Enforced | validation and routing tests |
| One deterministic output block per channel per process | Enforced | streaming, FDC, PFB, and acceptance tests |
| Rates and timing live in the resolved plan, not callbacks | Enforced | plan contract tests and [facade documentation](../facade.md) |
| Raw history is not copied into PFB FFT input | Enforced | PFB production-flow and diagnostics semantics tests |
| History never produces duplicate output | Enforced | exact-count and process-partition tests |
| Rational timing metadata handles fractional offsets in the plan | Enforced | `RationalSampleOffset` and contract/reference alignment tests |
| Steady-state processing has no managed allocations | Enforced | FDC/PFB/diagnostics tests and retained BDN results |
| FFTW planning never occurs in `Process` | Enforced | FFTW plan-cache/runtime design and execution tests |
| Both engines match the independent DDC | Enforced | FDC/PFB signal acceptance fixtures |
| Benchmarks cover primitives, FFTW, FDC, PFB, SIMD backends and gated Step 14 candidates | Enforced | retained 18-case scalar/AVX2/AVX-512 engine BDN plus PFB FIR, FDC extraction, rotator, FoldAware end-to-end and selected-bin crossover comparisons |
| Target 100 MS/s profile has a recorded realtime result | Deferred | Current stored profile is configuration-specific and explicitly makes no 100 MS/s realtime claim |
| FFTW licensing/distribution is documented | Enforced | managed library is MIT; FFTW DLL is separately licensed and excluded from NuGet; isolated package consumer passes with a separately supplied runtime |
| README has a minimal request/process/output example | Enforced | self-contained example in [`README.md`](../../README.md) |
| Every `Auto` decision is backed by a stored profile | Enforced | exact environment/request matching in `StrategyProfileSelector`, stored `strategy-profile-v1.json`, plan key/explanation; unmatched cases throw |

## Release blockers and next gates

The Conservative/explicit FoldAware scalar/AVX2/AVX-512 implementation is correctness- and benchmark-audited, but the
full project Definition of Done is not complete. The remaining gate is:

1. a target 100 MS/s end-to-end profile before any realtime claim.

Selected-bin/direct-DFT is intentionally not a release blocker: its stored crossover is
shape-limited, and the representative stage profile attributes only about 9.1% of PFB time
to FFTW. The prototype therefore remains internal and unwired.

The detailed fixture map and reproducible commands are in
[`manifest.md`](manifest.md). Stored scalar signal and scalar/AVX2/AVX-512 performance summaries are under
`artifacts/signal-validation/` and `artifacts/benchmarks/`.
