# IqChannelizer

Streaming complex-IQ channelization with interchangeable FDC and generalized PFB strategies.

The current milestone provides the public request/plan/streaming contracts, deterministic planning, a multi-decimation FFTW overlap-save FDC with one shared forward FFT, grouped short inverse transforms and validated Kaiser anti-alias filters, plus a generalized batched `K/H` PFB with Conservative and explicit FoldAware `P > 1` Kaiser prototypes, absolute frame correction, unique-bin fan-out and per-channel power-of-two fine decimation. The PFB planner enumerates feasible `K/H/FramesPerBatch` shapes, validates exact filters, and can select non-2× oversampling. Scalar reference transforms, conservative response/folding validation, and BenchmarkDotNet entry points are included. SIMD is resolved once at engine creation: `Auto` uses measured AVX2/FMA kernels for FDC and AVX-512F for PFB when those ISAs are available, then falls back through AVX2 to scalar. Every backend may also be forced explicitly. Channelizer strategy `Auto` is available only for an exact versioned benchmark-profile match and rejects unknown environments/shapes; the production selected-bin path and realtime claims remain behind separate data gates.

For source-checkout builds and tests, the pinned Windows x64 FFTW binary is copied beside the managed assembly automatically. It is intentionally excluded from the managed-only NuGet package: NuGet consumers must supply a compatible native FFTW runtime beside the application or managed assembly themselves. FFTW plans and aligned native buffers are created once with the engine and disposed with it; no planning occurs in `Process`.

IqChannelizer is licensed under the [MIT License](LICENSE). Runtime support, bounded cache/wisdom policy, exact FFTW binary provenance, and the separate FFTW licensing obligations are documented in [docs/fftw-runtime.md](docs/fftw-runtime.md). The managed-only NuGet layout and its clean-consumer verification are documented in [docs/release-policy.md](docs/release-policy.md).

```csharp
using System;
using System.Linq;
using IqChannelizer;
using IqChannelizer.Abstractions;

var request = new ChannelizerRequest(
    InputSampleRateHz: 1_000_000,
    Channels: [new ChannelRequest(1, 125_000, 20_000, 10_000)],
    Strategy: ChannelizerStrategy.Pfb,
    InputBlocks: new InputBlockConstraints(256, 1024),
    Hints: new ChannelizerImplementationHints(Simd: SimdPreference.Auto));

using var channelizer = ChannelizerFactory.Create(request);

// Inspect the immutable resolved shape before sizing the caller-owned ring buffer.
var requirements = channelizer.InputRequirements;
var resolvedChannel = channelizer.Plan.Channels.Single();
Console.WriteLine($"Output: {resolvedChannel.OutputSampleRateHz:R} Hz, " +
                  $"{resolvedChannel.OutputSamplesPerProcess} samples/call");

var historyAndChunk = new ComplexF[requirements.InputSize];
// On every call, populate [0..HistorySize) with preceding IQ and the remainder
// with exactly ChunkSize new samples. Zero history is valid at a new stream boundary.
var sink = new CountingSink();
long firstNewSampleIndex = 0;
channelizer.Process(historyAndChunk, firstNewSampleIndex, sink);
firstNewSampleIndex += requirements.ChunkSize;

sealed class CountingSink : IChannelOutputSink
{
    public long SamplesReceived { get; private set; }

    public void Write(int channelId, ReadOnlySpan<ComplexF> samples) =>
        SamplesReceived = checked(SamplesReceived + samples.Length);
}
```

## Command-line application

The `IqChannelizer.Cli` project channelizes a SigMF or stereo I/Q WAV recording and writes
one complex baseband recording per requested channel. It uses `System.CommandLine`; repeat
`--channel` or place several channel values after one occurrence:

```powershell
dotnet run --project src/IqChannelizer.Cli -- `
  --input recording.sigmf `
  --output channels `
  --strategy fdc `
  --channel 1:125000:20000:10000 `
  --channel 2:-175000:12000:6000
```

The channel syntax is
`ID:CENTER:PASSBAND:TRANSITION[:STOPBAND[:RIPPLE[:MIN_RATE[:PREFERRED_RATE]]]]`,
with frequencies and rates in Hz and attenuation/ripple in dB. Numeric values use invariant
culture (a dot as the decimal separator). Run with `--help` to see the strategy, SIMD, block,
and output options.

The completion summary reports aggregate average and per-block minimum/maximum DSP throughput
relative to the input sample rate (`1.00x` is realtime), together with the average absolute
throughput. The timing covers `IStreamingChannelizer.Process` only, excluding input and output
file I/O; a padded final block is counted at the full DSP block size.

Input may be a SigMF pair (`.sigmf-meta` plus `.sigmf-data`), a `.sigmf` tar archive, or a
two-channel WAV whose left/right samples are I/Q. Common complex integer and floating-point
SigMF datatypes and PCM/IEEE-float WAV encodings are converted to `ComplexF`. Output defaults
to a `cf32_le` SigMF pair per channel; `--output-format wav` writes stereo float32 I/Q WAV when
the resolved output rate is an integer. Existing output files are never overwritten.

The caller owns the ring buffer and supplies exactly `[HistorySize | ChunkSize]`. Each `Process` call writes exactly one deterministic block per requested channel in request order.

Without a forced `FdcDecimationFactor`, the FDC planner selects a power-of-two decimation per channel from its occupied bandwidth and minimum/preferred output rates, aligns shared history/chunk requirements to the largest selected factor, and groups channels with equal short-IFFT lengths. A forced factor remains a global deterministic override and is validated for every channel.

Without complete forced PFB hints, the deterministic feasibility planner searches power-of-two `K` values, arbitrary integer `H`, and bounded frame counts. It checks single-bin geometry, periodic residuals, required output rates, chunk bounds, Conservative prototype response, and every fine-stage response before returning a plan. Feasible exact-bin Conservative layouts also consider the preceding critical `H=K` grid, avoiding oversampling when that grid and the requested frame batch both fit. Hop shortlisting happens only after block feasibility, so a forced frame count cannot hide a valid smaller hop. Until a target-hardware benchmark profile exists, the plan contains a warning and `BenchmarkProfileKey` remains null. Supplying all three PFB shape hints is a deterministic override.

PFB prototype design defaults to `PfbPrototypeDesignMode.Conservative`. A measured
`PfbPrototypeDesignMode.FoldAware` mode can be requested explicitly through
`ChannelizerImplementationHints.PfbPrototypeDesign`; it uses a wider alias-safe transition,
while retaining folded-response and blocker-sweep validation. It is not selected automatically
until a versioned planner profile is available. The selected-bin/direct-DFT prototype remains
benchmark-only because its measured crossover does not justify another production path yet.

`ChannelizerStrategy.Auto` never applies an unmeasured heuristic. The accepted
`equal-spec-1m-10k-q1-8-32-v1` profile covers its recorded Windows x64 CPU/runtime/FFTW
environment and exact 1 MS/s, 4096-sample, 1/8/32-channel request family. A match resolves
to FDC and records the profile key plus explanation in the plan; every other request throws
an actionable `NotSupportedException` and must force `Fdc` or `Pfb` explicitly.

On the named Ryzen 5 8500G/.NET 10.0.11/FFTW 3.3.5 target profile, FoldAware PFB
`K=4096,H=2048,F=16` sustained 199.17 MS/s for the recorded eight-channel 100 MHz
configuration with zero managed allocations; FDC sustained 8.02 MS/s. These are retained,
configuration-specific results, not a portable realtime guarantee.

The fine stage selects a per-channel power-of-two factor that divides the frame batch. An exact-bin, factor-one Conservative channel bypasses the fine FIR only when the validated shared prototype already satisfies that channel; FoldAware and residual-offset channels retain their per-channel filter. Zero-residual filtered routes also skip the rotation buffer. Channels sharing a coarse bin reuse one gathered stream before independent residual rotation, filtering, and decimation; FIR startup state is represented by the resolved group-delay metadata.

`ResolvedChannelizerPlan` contains immutable engine and per-channel rates, FFT/PFB dimensions, exact output counts, group delay, and the first-output offset in input-sample units. The offset is relative to `firstNewSampleIndex`: for the causal symmetric FDC FIR it is `-GroupDelayInputSamples`; for the PFB prototype it is `HopSize - 1 - GroupDelayInputSamples`.

After a stream discontinuity, call `Reset(nextFirstNewSampleIndex)` and provide fresh history (normally zero-filled at a new logical stream boundary) on the next `Process`. `Reset` establishes the exact absolute index accepted by that next call; it does not manufacture history. If the output sink throws, the engine is faulted because some blocks may already have been observed; no further processing is accepted until `Reset`. Calling either `Process` or `Reset` after disposal throws `ObjectDisposedException`.

`Reset` preserves the resolved DSP shape. To change channels, rates, strategy, or implementation hints, create and inspect a new engine, then swap it at an application-defined stream boundary. The complete facade, buffer-lifetime, and reconfiguration contract is in [docs/facade.md](docs/facade.md).

Allocation-free block counters are available through `channelizer.Diagnostics`. They are disabled by default; `Counters` enables counts and `StageTiming` additionally records stage timings, maximum latency and the latest realtime margin. Reset/failure semantics and field units are documented in [docs/diagnostics.md](docs/diagnostics.md).

The tracked acceptance map and verification commands are in [docs/acceptance/manifest.md](docs/acceptance/manifest.md); the current Definition-of-Done audit is in [docs/acceptance/report.md](docs/acceptance/report.md). To list or run benchmarks:

```powershell
dotnet run --project benchmarks/IqChannelizer.Benchmarks -c Release -- --list flat
dotnet run --project benchmarks/IqChannelizer.Benchmarks -c Release
```

Benchmark results are environment-specific. A Dry job is only a harness smoke test and must not be used for realtime or `Auto` decisions.
