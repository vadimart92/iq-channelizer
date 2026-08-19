# Unified facade and streaming lifecycle

`ChannelizerFactory.Create` is the single public entry point for the FDC and generalized
PFB engines. Creation validates the request, resolves the complete DSP shape, creates all
native FFTW plans and buffers, and returns an `IStreamingChannelizer`. `Process` never
performs planning.

## Inspect before streaming

Creation exposes immutable snapshots through `Plan` and `InputRequirements`. Inspect them
before allocating the caller-owned ring buffer or configuring downstream consumers:

- `InputRequirements` gives the exact `[HistorySize | ChunkSize]` span shape;
- engine fields identify the selected strategy, FFT/PFB shape, backend, working-set
  estimate, warnings, and any benchmark profile used;
- each channel entry gives the original opaque ID, actual output rate, exact samples per
  call, coarse/fine decomposition, filter IDs, and rational timing offsets.

The resolved channel and warning collections cannot be changed through a mutable
collection cast, and they do not track later mutations to the request's channel list.
Changing a copied record with a C# `with` expression creates a new record; it does not
reconfigure the engine.

PFB prototype design defaults to `PfbPrototypeDesignMode.Conservative`. Callers may set
`ChannelizerImplementationHints.PfbPrototypeDesign` to `FoldAware` before creation; the
resolved plan records the chosen mode. FoldAware is an explicit, validated choice and is
not selected automatically without a versioned planner profile.

## Process contract

For each call, the application supplies exactly `InputRequirements.InputSize` samples.
The first `HistorySize` values are preceding context and the final `ChunkSize` values are
new input. `firstNewSampleIndex` is the absolute index of the first new value, not the
start of the history span.

Successful calls must advance `firstNewSampleIndex` by exactly `ChunkSize`. Each call
invokes the sink once per requested channel, in request order, and the span length is the
channel's fixed `OutputSamplesPerProcess`. A sink span is engine-owned and valid only for
the duration of `Write`; copy it inside `Write` if it must outlive the callback.

An engine represents one serialized stream. Applications must serialize `Process`,
`Reset`, and `Dispose` calls for that engine and implement scheduling, ring-buffer
ownership, backpressure, and cross-thread delivery outside this library.

## Reset versus reconfiguration

`Reset(nextFirstNewSampleIndex)` clears stateful DSP history and establishes the only
absolute index accepted by the next call. It is appropriate for a discontinuity, a new
logical stream boundary, or recovery after a sink/processing failure. The caller must
still provide valid history; reset does not synthesize samples. Lifetime diagnostics
counters remain cumulative, while current fault status is cleared.

Reset does not change the resolved plan. To change any channel, rate, strategy, input
constraint, or implementation hint:

1. create a new engine from the new request outside the active engine's processing path;
2. inspect its plan and allocate any new input/output storage;
3. stop delivery to the old engine at an application-defined boundary;
4. start the new engine with the desired absolute origin and valid history;
5. dispose the old engine after no callback can still use it.

There is intentionally no in-place reconfiguration API: it would mix FFTW planning and
buffer replacement with the streaming contract. `Auto` resolves only when the current
environment and exact request family match a versioned comparative profile. The resolved
plan records `BenchmarkProfileKey` and a human-readable decision warning; unmatched requests
are rejected and must select `Fdc` or `Pfb` explicitly.

## Failures and diagnostics

Invalid span length and discontinuity are rejected before DSP work. If processing or a
sink callback throws, the engine is faulted because the sink may already have observed a
partial set of channel blocks. Further processing is rejected until `Reset`.

Diagnostics are disabled by default. `Counters` and `StageTiming` are creation-time
options; their units, fault/reset behavior, and realtime-margin limitations are described
in [diagnostics.md](diagnostics.md).

See the repository [README](../README.md) for a minimal complete request, plan-inspection,
process, and sink example.
