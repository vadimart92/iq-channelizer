using IqChannelizer.Abstractions;

namespace IqChannelizer.Runtime;

internal abstract class StreamingEngineBase : IStreamingChannelizer
{
    private bool _hasExpectedIndex;
    private long _expectedFirstNewSampleIndex;
    private bool _disposed;

    protected StreamingEngineBase(ResolvedChannelizerPlan plan)
    {
        Plan = plan;
        InputRequirements = plan.InputRequirements;
    }

    public ResolvedChannelizerPlan Plan { get; }
    public InputRequirements InputRequirements { get; }

    public void Process(ReadOnlySpan<ComplexF> historyAndChunk, long firstNewSampleIndex, IChannelOutputSink output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(output);
        if (historyAndChunk.Length != InputRequirements.InputSize)
        {
            throw new ArgumentException($"Expected exactly {InputRequirements.InputSize} input samples.", nameof(historyAndChunk));
        }

        if (_hasExpectedIndex && firstNewSampleIndex != _expectedFirstNewSampleIndex)
        {
            throw new InvalidOperationException($"Input discontinuity: expected { _expectedFirstNewSampleIndex}, got {firstNewSampleIndex}.");
        }

        ProcessCore(historyAndChunk, firstNewSampleIndex, output);
        _expectedFirstNewSampleIndex = checked(firstNewSampleIndex + InputRequirements.ChunkSize);
        _hasExpectedIndex = true;
    }

    protected abstract void ProcessCore(ReadOnlySpan<ComplexF> input, long firstNewSampleIndex, IChannelOutputSink output);

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
