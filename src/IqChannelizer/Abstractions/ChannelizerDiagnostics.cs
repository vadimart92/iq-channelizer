using System.Diagnostics;

namespace IqChannelizer.Abstractions;

public enum ChannelizerFailureKind
{
    None,
    FftwExecution,
    OutputSink,
    Processing
}

public readonly record struct ChannelizerDiagnosticsSnapshot(
    DiagnosticsMode Mode,
    long InputSamplesConsumed,
    long ChunksProcessed,
    long RejectedInputLengthCount,
    long RejectedDiscontinuityCount,
    long TotalOutputSamples,
    long FdcInputCopyBytes,
    long FdcInputCopyElapsedTicks,
    long PfbPolyphaseInputSamples,
    long PfbPolyphaseElapsedTicks,
    long FftwExecutionElapsedTicks,
    long ChannelProcessingElapsedTicks,
    long OutputDeliveryElapsedTicks,
    long ProcessingElapsedTicks,
    long MaximumProcessingLatencyTicks,
    double CurrentRealtimeMargin,
    long FftwExecutionFailureCount,
    long FailedProcessCount,
    bool IsFaulted,
    ChannelizerFailureKind LastFailureKind,
    long ReconfigurationCount);

public sealed class ChannelizerDiagnostics
{
    private readonly DiagnosticsMode _mode;
    private readonly double _inputSampleRateHz;
    private readonly int _chunkSize;
    private readonly int[] _channelIds;
    private readonly long[] _channelOutputSamples;
    private long _inputSamplesConsumed;
    private long _chunksProcessed;
    private long _rejectedInputLengthCount;
    private long _rejectedDiscontinuityCount;
    private long _totalOutputSamples;
    private long _fdcInputCopyBytes;
    private long _fdcInputCopyElapsedTicks;
    private long _pfbPolyphaseInputSamples;
    private long _pfbPolyphaseElapsedTicks;
    private long _fftwExecutionElapsedTicks;
    private long _channelProcessingElapsedTicks;
    private long _outputDeliveryElapsedTicks;
    private long _processingElapsedTicks;
    private long _maximumProcessingLatencyTicks;
    private double _currentRealtimeMargin;
    private long _fftwExecutionFailureCount;
    private long _failedProcessCount;
    private int _isFaulted;
    private int _lastFailureKind;
    private long _reconfigurationCount;

    internal ChannelizerDiagnostics(ResolvedChannelizerPlan plan, DiagnosticsMode mode)
    {
        _mode = mode;
        _inputSampleRateHz = plan.InputSampleRateHz;
        _chunkSize = plan.InputRequirements.ChunkSize;
        _channelIds = plan.Channels.Select(channel => channel.ChannelId).ToArray();
        _channelOutputSamples = new long[_channelIds.Length];
    }

    public DiagnosticsMode Mode => _mode;
    internal bool IsEnabled => _mode != DiagnosticsMode.Disabled;
    internal bool IsTimingEnabled => _mode == DiagnosticsMode.StageTiming;

    public ChannelizerDiagnosticsSnapshot Snapshot => new(
        _mode,
        Interlocked.Read(ref _inputSamplesConsumed),
        Interlocked.Read(ref _chunksProcessed),
        Interlocked.Read(ref _rejectedInputLengthCount),
        Interlocked.Read(ref _rejectedDiscontinuityCount),
        Interlocked.Read(ref _totalOutputSamples),
        Interlocked.Read(ref _fdcInputCopyBytes),
        Interlocked.Read(ref _fdcInputCopyElapsedTicks),
        Interlocked.Read(ref _pfbPolyphaseInputSamples),
        Interlocked.Read(ref _pfbPolyphaseElapsedTicks),
        Interlocked.Read(ref _fftwExecutionElapsedTicks),
        Interlocked.Read(ref _channelProcessingElapsedTicks),
        Interlocked.Read(ref _outputDeliveryElapsedTicks),
        Interlocked.Read(ref _processingElapsedTicks),
        Interlocked.Read(ref _maximumProcessingLatencyTicks),
        Volatile.Read(ref _currentRealtimeMargin),
        Interlocked.Read(ref _fftwExecutionFailureCount),
        Interlocked.Read(ref _failedProcessCount),
        Volatile.Read(ref _isFaulted) != 0,
        (ChannelizerFailureKind)Volatile.Read(ref _lastFailureKind),
        Interlocked.Read(ref _reconfigurationCount));

    public long GetOutputSamples(int channelId)
    {
        for (var index = 0; index < _channelIds.Length; index++)
        {
            if (_channelIds[index] == channelId)
            {
                return Interlocked.Read(ref _channelOutputSamples[index]);
            }
        }

        throw new ArgumentOutOfRangeException(nameof(channelId), $"Unknown channel id {channelId}.");
    }

    internal long BeginTiming() => IsTimingEnabled ? Stopwatch.GetTimestamp() : 0;

    internal void RecordRejectedInputLength()
    {
        if (IsEnabled) Interlocked.Increment(ref _rejectedInputLengthCount);
    }

    internal void RecordRejectedDiscontinuity()
    {
        if (IsEnabled) Interlocked.Increment(ref _rejectedDiscontinuityCount);
    }

    internal void RecordProcessSucceeded(long startedAt)
    {
        if (!IsEnabled) return;
        Interlocked.Add(ref _inputSamplesConsumed, _chunkSize);
        Interlocked.Increment(ref _chunksProcessed);
        if (!IsTimingEnabled) return;

        var elapsed = Stopwatch.GetTimestamp() - startedAt;
        Interlocked.Add(ref _processingElapsedTicks, elapsed);
        UpdateMaximum(ref _maximumProcessingLatencyTicks, elapsed);
        var availableTicks = _chunkSize * (double)Stopwatch.Frequency / _inputSampleRateHz;
        Volatile.Write(ref _currentRealtimeMargin, elapsed == 0 ? double.PositiveInfinity : availableTicks / elapsed);
    }

    internal void RecordOutput(int channelIndex, int sampleCount, long elapsedTicks)
    {
        if (!IsEnabled) return;
        Interlocked.Add(ref _channelOutputSamples[channelIndex], sampleCount);
        Interlocked.Add(ref _totalOutputSamples, sampleCount);
        if (IsTimingEnabled) Interlocked.Add(ref _outputDeliveryElapsedTicks, elapsedTicks);
    }

    internal void RecordFdcInputCopy(int bytes, long elapsedTicks)
    {
        if (!IsEnabled) return;
        Interlocked.Add(ref _fdcInputCopyBytes, bytes);
        if (IsTimingEnabled) Interlocked.Add(ref _fdcInputCopyElapsedTicks, elapsedTicks);
    }

    internal void RecordPfbPolyphase(int inputSamples, long elapsedTicks)
    {
        if (!IsEnabled) return;
        Interlocked.Add(ref _pfbPolyphaseInputSamples, inputSamples);
        if (IsTimingEnabled) Interlocked.Add(ref _pfbPolyphaseElapsedTicks, elapsedTicks);
    }

    internal void RecordFftwExecution(long elapsedTicks)
    {
        if (IsTimingEnabled) Interlocked.Add(ref _fftwExecutionElapsedTicks, elapsedTicks);
    }

    internal void RecordChannelProcessing(long elapsedTicks)
    {
        if (IsTimingEnabled) Interlocked.Add(ref _channelProcessingElapsedTicks, elapsedTicks);
    }

    internal void RecordFftwFailure()
    {
        if (!IsEnabled) return;
        Interlocked.Increment(ref _fftwExecutionFailureCount);
        Volatile.Write(ref _lastFailureKind, (int)ChannelizerFailureKind.FftwExecution);
    }

    internal void RecordOutputSinkFailure()
    {
        if (IsEnabled) Volatile.Write(ref _lastFailureKind, (int)ChannelizerFailureKind.OutputSink);
    }

    internal void RecordProcessFailure()
    {
        if (!IsEnabled) return;
        if (Volatile.Read(ref _lastFailureKind) == (int)ChannelizerFailureKind.None)
        {
            Volatile.Write(ref _lastFailureKind, (int)ChannelizerFailureKind.Processing);
        }

        Volatile.Write(ref _isFaulted, 1);
        Interlocked.Increment(ref _failedProcessCount);
    }

    internal void RecordReconfiguration()
    {
        if (!IsEnabled) return;
        Volatile.Write(ref _isFaulted, 0);
        Volatile.Write(ref _lastFailureKind, (int)ChannelizerFailureKind.None);
        Interlocked.Increment(ref _reconfigurationCount);
    }

    private static void UpdateMaximum(ref long target, long candidate)
    {
        var current = Interlocked.Read(ref target);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current) return;
            current = observed;
        }
    }
}
