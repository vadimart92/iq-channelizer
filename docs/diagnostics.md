# Diagnostics and observability

Diagnostics are disabled by default. Opt in at engine creation with
`ChannelizerImplementationHints.Diagnostics`:

- `Disabled` keeps every counter and timer at zero;
- `Counters` records block-level counts without reading the clock;
- `StageTiming` adds `Stopwatch`-tick stage timings, maximum block latency, and the
  realtime margin of the latest completed block.

```csharp
var hints = new ChannelizerImplementationHints(
    Simd: SimdPreference.Scalar,
    Diagnostics: DiagnosticsMode.StageTiming);

using var channelizer = ChannelizerFactory.Create(request with { Hints = hints });
var status = channelizer.Diagnostics.Snapshot;
var channelSamples = channelizer.Diagnostics.GetOutputSamples(channelId);
```

`Snapshot` is a value type and `GetOutputSamples` uses the channel table allocated when
the engine is created. Reading either API and updating enabled diagnostics do not allocate
managed memory in the steady-state processing path.

Counters are cumulative for the lifetime of the engine. `Reset` establishes a new stream
origin, clears the current fault status, and increments `ReconfigurationCount`; it does not
clear historical counters. A failed output callback leaves `IsFaulted` set until `Reset`,
while `FailedProcessCount` and the successfully delivered per-channel counts remain
available for incident analysis.

Timing values use `Stopwatch` ticks and can be converted with `Stopwatch.Frequency`.
`CurrentRealtimeMargin` is:

```text
(ChunkSize / InputSampleRateHz) / latest successful Process duration
```

A value above one means that the latest block completed faster than its input duration.
This is observability, not a realtime guarantee; use the full statistical benchmark job
before making capacity claims.

Engine-specific input-stage fields have intentionally distinct semantics:

- FDC records bytes copied from `[history | chunk]` into the forward FFTW input;
- PFB records newly consumed input samples processed by the polyphase stage. Raw history
  remains FIR context and is not copied into the FFTW input.

No tracing or logging occurs in the sample/frame loops. Applications can sample snapshots
at their own cadence.
