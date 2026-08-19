# IqChannelizer

Streaming complex-IQ channelization with interchangeable FDC and generalized PFB strategies.

The current milestone provides the public request/plan/streaming contracts, deterministic planning, a multi-decimation FFTW overlap-save FDC with one shared forward FFT, grouped short inverse transforms and validated Kaiser anti-alias filters, plus a generalized batched `K/H` PFB with a Conservative `P > 1` Kaiser prototype, absolute frame correction, unique-bin fan-out and per-channel power-of-two fine decimation. The PFB planner enumerates feasible `K/H/FramesPerBatch` shapes, validates exact filters, and can select non-2× oversampling. Scalar reference transforms, conservative response/folding validation, and BenchmarkDotNet entry points are included. SIMD, `Auto`, FoldAware/selected-bin variants, and realtime claims are intentionally not included yet.

The Windows x64 FFTW binary is copied beside consuming applications automatically. FFTW plans and aligned native buffers are created once with the engine and disposed with it; no planning occurs in `Process`.

Runtime support, cache/wisdom policy, exact binary provenance, and licensing obligations are documented in [docs/fftw-runtime.md](docs/fftw-runtime.md). Redistributable packaging remains disabled until the release decision in [docs/release-policy.md](docs/release-policy.md) is approved.

```csharp
var request = new ChannelizerRequest(
    InputSampleRateHz: 1_000_000,
    Channels: [new ChannelRequest(1, 125_000, 20_000, 10_000)],
    Strategy: ChannelizerStrategy.Pfb,
    InputBlocks: new InputBlockConstraints(256, 1024),
    Hints: new ChannelizerImplementationHints(Simd: SimdPreference.Scalar));

using var channelizer = ChannelizerFactory.Create(request);
var requirements = channelizer.InputRequirements;
channelizer.Process(historyAndChunk, firstNewSampleIndex, outputSink);
```

The caller owns the ring buffer and supplies exactly `[HistorySize | ChunkSize]`. Each `Process` call writes exactly one deterministic block per requested channel in request order.

Without a forced `FdcDecimationFactor`, the FDC planner selects a power-of-two decimation per channel from its occupied bandwidth and minimum/preferred output rates, aligns shared history/chunk requirements to the largest selected factor, and groups channels with equal short-IFFT lengths. A forced factor remains a global deterministic override and is validated for every channel.

Without complete forced PFB hints, the deterministic feasibility planner searches power-of-two `K` values, arbitrary integer `H`, and bounded frame counts. It checks single-bin geometry, periodic residuals, required output rates, chunk bounds, Conservative prototype response, and every fine-stage response before returning a plan. Until a target-hardware benchmark profile exists, the plan contains a warning and `BenchmarkProfileKey` remains null. Supplying all three PFB shape hints is a deterministic override.

The fine stage selects a per-channel power-of-two factor that divides the frame batch. A real per-channel FIR is retained even when that factor is one, unless the requested stop edge is at or beyond the coarse Nyquist boundary. Channels sharing a coarse bin reuse one gathered stream before independent residual rotation, filtering, and decimation; FIR startup state is represented by the resolved group-delay metadata.

`ResolvedChannelizerPlan` contains immutable engine and per-channel rates, FFT/PFB dimensions, exact output counts, group delay, and the first-output offset in input-sample units. The offset is relative to `firstNewSampleIndex`: for the causal symmetric FDC FIR it is `-GroupDelayInputSamples`; for the PFB prototype it is `HopSize - 1 - GroupDelayInputSamples`.

After a stream discontinuity, call `Reset(nextFirstNewSampleIndex)` and provide fresh history (normally zero-filled at a new logical stream boundary) on the next `Process`. `Reset` establishes the exact absolute index accepted by that next call; it does not manufacture history. If the output sink throws, the engine is faulted because some blocks may already have been observed; no further processing is accepted until `Reset`. Calling either `Process` or `Reset` after disposal throws `ObjectDisposedException`.

Allocation-free block counters are available through `channelizer.Diagnostics`. They are disabled by default; `Counters` enables counts and `StageTiming` additionally records stage timings, maximum latency and the latest realtime margin. Reset/failure semantics and field units are documented in [docs/diagnostics.md](docs/diagnostics.md).

The tracked acceptance map and verification commands are in [docs/acceptance/manifest.md](docs/acceptance/manifest.md). To list or run benchmarks:

```powershell
dotnet run --project benchmarks/IqChannelizer.Benchmarks -c Release -- --list flat
dotnet run --project benchmarks/IqChannelizer.Benchmarks -c Release
```

Benchmark results are environment-specific. A Dry job is only a harness smoke test and must not be used for realtime or `Auto` decisions.
