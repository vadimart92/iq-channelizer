# IqChannelizer

Streaming complex-IQ channelization with interchangeable FDC and generalized PFB strategies.

The current milestone provides the public request/plan/streaming contracts, deterministic planning, a multi-decimation FFTW overlap-save FDC with one shared forward FFT, grouped short inverse transforms and validated Kaiser anti-alias filters, plus a generalized batched `K/H` PFB with a Conservative `P > 1` Kaiser prototype, absolute frame correction, unique-bin fan-out and per-channel power-of-two fine decimation. Scalar reference transforms and standalone filter-design/response/folding primitives are included. Generalized PFB candidate planning, SIMD, `Auto`, and realtime claims are not included yet.

The Windows x64 FFTW binary is copied beside consuming applications automatically. FFTW plans and aligned native buffers are created once with the engine and disposed with it; no planning occurs in `Process`.

Runtime support, cache/wisdom policy, exact binary provenance, and release licensing obligations are documented in [docs/fftw-runtime.md](docs/fftw-runtime.md).

```csharp
var request = new ChannelizerRequest(
    InputSampleRateHz: 1_000_000,
    Channels: [new ChannelRequest(1, 125_000, 20_000, 10_000)],
    Strategy: ChannelizerStrategy.Pfb,
    InputBlocks: new InputBlockConstraints(256, 1024),
    Hints: new ChannelizerImplementationHints(
        PfbFftSize: 64,
        PfbHopSize: 32,
        PfbFramesPerBatch: 8,
        Simd: SimdPreference.Scalar));

using var channelizer = ChannelizerFactory.Create(request);
var requirements = channelizer.InputRequirements;
channelizer.Process(historyAndChunk, firstNewSampleIndex, outputSink);
```

The caller owns the ring buffer and supplies exactly `[HistorySize | ChunkSize]`. Each `Process` call writes exactly one deterministic block per requested channel in request order.

Without a forced `FdcDecimationFactor`, the FDC planner selects a power-of-two decimation per channel from its occupied bandwidth and minimum/preferred output rates, aligns shared history/chunk requirements to the largest selected factor, and groups channels with equal short-IFFT lengths. A forced factor remains a global deterministic override and is validated for every channel.

The current PFB path uses forced/default `K`, `H`, and `FramesPerBatch`, then selects a per-channel power-of-two fine factor that divides the frame batch and satisfies the channel’s output-rate constraints. Channels sharing a coarse bin reuse one gathered coarse stream before independent residual rotation, fine filtering, and decimation.

`ResolvedChannelizerPlan` contains immutable engine and per-channel rates, FFT/PFB dimensions, exact output counts, group delay, and the first-output offset in input-sample units. The offset is relative to `firstNewSampleIndex`: for the causal symmetric FDC FIR it is `-GroupDelayInputSamples`; for the PFB prototype it is `HopSize - 1 - GroupDelayInputSamples`.

After a stream discontinuity, call `Reset(nextFirstNewSampleIndex)` and provide fresh history (normally zero-filled at a new logical stream boundary) on the next `Process`. `Reset` establishes the exact absolute index accepted by that next call; it does not manufacture history. Calling either `Process` or `Reset` after disposal throws `ObjectDisposedException`.
