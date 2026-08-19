# Definition-of-Done acceptance report

This report audits section 14 of [`implementation-plan.md`](../../implementation-plan.md)
against the scalar Conservative milestone at commit `23b05b0` plus the current Step 15
working tree. It records capability status; it is not a redistributable-release approval
or a portable realtime claim.

Verification date: **2026-08-19**. Release suite: **170 passed, 0 failed, 0 skipped**.

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
| Pre-FFT circular shift equals explicit post-FFT correction | Enforced | `PfbAlgebraTests` |
| SIMD PFB FIR stores directly into rotated FFTW input | Deferred | Scalar direct-store path is enforced; SIMD requires explicit owner permission |
| AVX2/FMA hot-kernel backend exists | Deferred | Step 13 permission gate |
| AVX-512 exists where worthwhile or has benchmark evidence against it | Deferred | No permitted AVX-512 implementation/comparison yet |
| Scalar fallback exists | Enforced | Both production engines and scalar-vs-reference tests |
| ISA dispatch is outside inner loops | Guarded | Only scalar is selectable; forced AVX2/AVX-512 is rejected |
| FDC inverse normalization is explicit and amplitude-tested | Enforced | `FftwFdcEngine`, `FdcOverlapSaveTests` |
| Filter validation includes folded alias response | Enforced | filter tests and `SignalValidationAcceptanceTests` |
| Conservative and FoldAware designs can be compared safely | Deferred | FoldAware remains disabled pending accepted blocker/correctness data |
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
| Benchmarks cover primitives, FFTW, FDC, PFB, and SIMD backends | Deferred | Scalar primitive/FFTW/FDC/PFB statistical baseline exists; SIMD backend is gated |
| Target 100 MS/s profile has a recorded realtime result | Deferred | Current stored profile is configuration-specific and explicitly makes no 100 MS/s realtime claim |
| FFTW licensing/distribution is documented | Enforced | `docs/fftw-runtime.md` and `docs/release-policy.md`; actual distribution remains blocked |
| README has a minimal request/process/output example | Enforced | self-contained example in [`README.md`](../../README.md) |
| Every `Auto` decision is backed by a stored profile | Guarded | `Auto` makes no decisions and throws until a versioned comparative profile exists |

## Release blockers and next gates

The scalar Conservative implementation is correctness- and benchmark-audited, but the
full project Definition of Done is not complete. The remaining gates are:

1. explicit owner permission before Step 13 SIMD work;
2. accepted comparative correctness/performance data before FoldAware or selected-bin work;
3. a target 100 MS/s end-to-end profile before any realtime claim;
4. a versioned strategy-comparison profile before enabling `Auto`; and
5. a release-owner FFTW licensing/distribution decision before packaging.

The detailed fixture map and reproducible commands are in
[`manifest.md`](manifest.md). Stored scalar signal and performance summaries are under
`artifacts/signal-validation/` and `artifacts/benchmarks/`.
