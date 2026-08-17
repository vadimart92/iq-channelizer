# IqChannelizer

Streaming complex-IQ channelization with interchangeable FDC and generalized PFB strategies.

The current milestone provides the public request/plan/streaming contracts, deterministic planning, FFTW single-precision FDC, generalized batched `K/H` PFB with absolute frame correction, scalar reference transforms, and tests. It deliberately does not include SIMD, `Auto`, production filter design, or realtime claims.

The Windows x64 FFTW binary is copied beside consuming applications automatically. FFTW plans and aligned native buffers are created once with the engine and disposed with it; no planning occurs in `Process`.

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

`ResolvedChannelizerPlan` contains immutable engine and per-channel rates, FFT/PFB dimensions, exact output counts, group delay, and the first-output offset in input-sample units. The offset is relative to `firstNewSampleIndex`: it is currently `0` for the length-one FDC fixture and `HopSize - 1` for the one-tap-per-phase PFB fixture. Plan warnings explicitly identify these non-production filters.

After a stream discontinuity, call `Reset(nextFirstNewSampleIndex)` and provide fresh history (normally zero-filled at a new logical stream boundary) on the next `Process`. `Reset` establishes the exact absolute index accepted by that next call; it does not manufacture history. Calling either `Process` or `Reset` after disposal throws `ObjectDisposedException`.
