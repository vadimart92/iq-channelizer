using IqChannelizer.Abstractions;

namespace IqChannelizer.Runtime;

internal abstract class StreamingEngineBase : IStreamingChannelizer
{
    private bool _hasExpectedIndex;
    private long _expectedFirstNewSampleIndex;
    private bool _faulted;
    private bool _disposed;

    protected StreamingEngineBase(ResolvedChannelizerPlan plan, DiagnosticsMode diagnosticsMode)
    {
        Plan = plan;
        InputRequirements = plan.InputRequirements;
        Diagnostics = new ChannelizerDiagnostics(plan, diagnosticsMode);
    }

    public ResolvedChannelizerPlan Plan { get; }
    public InputRequirements InputRequirements { get; }
    public ChannelizerDiagnostics Diagnostics { get; }

    public void Process(ReadOnlySpan<ComplexF> historyAndChunk, long firstNewSampleIndex, IChannelOutputSink output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(output);
        if (_faulted)
        {
            throw new InvalidOperationException("The channelizer is faulted after a failed Process call. Call Reset before processing more input.");
        }

        if (historyAndChunk.Length != InputRequirements.InputSize)
        {
            Diagnostics.RecordRejectedInputLength();
            throw new ArgumentException($"Expected exactly {InputRequirements.InputSize} input samples.", nameof(historyAndChunk));
        }

        if (_hasExpectedIndex && firstNewSampleIndex != _expectedFirstNewSampleIndex)
        {
            Diagnostics.RecordRejectedDiscontinuity();
            throw new InvalidOperationException($"Input discontinuity: expected {_expectedFirstNewSampleIndex}, got {firstNewSampleIndex}.");
        }

        var nextFirstNewSampleIndex = checked(firstNewSampleIndex + InputRequirements.ChunkSize);
        var processStartedAt = Diagnostics.BeginTiming();
        try
        {
            ProcessCore(historyAndChunk, firstNewSampleIndex, output);
        }
        catch
        {
            _faulted = true;
            Diagnostics.RecordProcessFailure();
            throw;
        }

        _expectedFirstNewSampleIndex = nextFirstNewSampleIndex;
        _hasExpectedIndex = true;
        Diagnostics.RecordProcessSucceeded(processStartedAt);
    }

    public void Reset(long nextFirstNewSampleIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ResetCore();
        _faulted = false;
        _expectedFirstNewSampleIndex = nextFirstNewSampleIndex;
        _hasExpectedIndex = true;
        Diagnostics.RecordReconfiguration();
    }

    protected void WriteOutput(IChannelOutputSink output, int channelIndex, ReadOnlySpan<ComplexF> samples)
    {
        var startedAt = Diagnostics.BeginTiming();
        try
        {
            output.Write(Plan.Channels[channelIndex].ChannelId, samples);
        }
        catch
        {
            Diagnostics.RecordOutputSinkFailure();
            throw;
        }

        var elapsed = Diagnostics.IsTimingEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() - startedAt : 0;
        Diagnostics.RecordOutput(channelIndex, samples.Length, elapsed);
    }

    protected abstract void ProcessCore(ReadOnlySpan<ComplexF> input, long firstNewSampleIndex, IChannelOutputSink output);

    protected virtual void ResetCore()
    {
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        DisposeCore();
        _disposed = true;
    }

    protected virtual void DisposeCore()
    {
    }
}
