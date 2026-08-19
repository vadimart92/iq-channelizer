using IqChannelizer.Abstractions;

namespace IqChannelizer.Tests;

public sealed class DiagnosticsTests
{
    [TestCase(ChannelizerStrategy.Fdc)]
    [TestCase(ChannelizerStrategy.Pfb)]
    public void CountersAreMonotonicAndExposePerChannelOutput(ChannelizerStrategy strategy)
    {
        using var engine = ChannelizerFactory.Create(Request(strategy, DiagnosticsMode.Counters));
        var input = new ComplexF[engine.InputRequirements.InputSize];
        var chunk = engine.InputRequirements.ChunkSize;

        Assert.That(() => engine.Process(input.AsSpan(1), 0, new TestSink()), Throws.ArgumentException);
        engine.Process(input, 0, new TestSink());
        Assert.That(() => engine.Process(input, chunk + 1L, new TestSink()), Throws.InvalidOperationException);
        engine.Process(input, chunk, new TestSink());
        var beforeReset = engine.Diagnostics.Snapshot;

        engine.Reset(10_000);
        engine.Process(input, 10_000, new TestSink());
        var afterReset = engine.Diagnostics.Snapshot;

        Assert.Multiple(() =>
        {
            Assert.That(afterReset.Mode, Is.EqualTo(DiagnosticsMode.Counters));
            Assert.That(afterReset.InputSamplesConsumed, Is.EqualTo(3L * chunk));
            Assert.That(afterReset.ChunksProcessed, Is.EqualTo(3));
            Assert.That(afterReset.RejectedInputLengthCount, Is.EqualTo(1));
            Assert.That(afterReset.RejectedDiscontinuityCount, Is.EqualTo(1));
            Assert.That(afterReset.ReconfigurationCount, Is.EqualTo(1));
            Assert.That(afterReset.FftwExecutionFailureCount, Is.Zero);
            Assert.That(afterReset.FailedProcessCount, Is.Zero);
            Assert.That(afterReset.IsFaulted, Is.False);
            Assert.That(afterReset.LastFailureKind, Is.EqualTo(ChannelizerFailureKind.None));
            Assert.That(afterReset.TotalOutputSamples,
                Is.EqualTo(3L * engine.Plan.Channels.Sum(channel => channel.OutputSamplesPerProcess)));
            Assert.That(engine.Diagnostics.GetOutputSamples(11),
                Is.EqualTo(3L * engine.Plan.Channels[0].OutputSamplesPerProcess));
            Assert.That(engine.Diagnostics.GetOutputSamples(22),
                Is.EqualTo(3L * engine.Plan.Channels[1].OutputSamplesPerProcess));
            Assert.That(afterReset.InputSamplesConsumed, Is.GreaterThan(beforeReset.InputSamplesConsumed));
            Assert.That(afterReset.ChunksProcessed, Is.GreaterThan(beforeReset.ChunksProcessed));
            Assert.That(afterReset.ProcessingElapsedTicks, Is.Zero);
            Assert.That(afterReset.CurrentRealtimeMargin, Is.Zero);
            Assert.That(() => engine.Diagnostics.GetOutputSamples(999), Throws.InstanceOf<ArgumentOutOfRangeException>());
        });
    }

    [TestCase(ChannelizerStrategy.Fdc)]
    [TestCase(ChannelizerStrategy.Pfb)]
    public void StageTimingReportsEngineSpecificWorkAndRealtimeMargin(ChannelizerStrategy strategy)
    {
        using var engine = ChannelizerFactory.Create(Request(strategy, DiagnosticsMode.StageTiming));
        var input = new ComplexF[engine.InputRequirements.InputSize];
        engine.Process(input, 0, new ChecksumSink());
        engine.Process(input, engine.InputRequirements.ChunkSize, new ChecksumSink());
        var snapshot = engine.Diagnostics.Snapshot;

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Mode, Is.EqualTo(DiagnosticsMode.StageTiming));
            Assert.That(snapshot.ProcessingElapsedTicks, Is.GreaterThan(0));
            Assert.That(snapshot.MaximumProcessingLatencyTicks, Is.GreaterThan(0));
            Assert.That(snapshot.MaximumProcessingLatencyTicks, Is.LessThanOrEqualTo(snapshot.ProcessingElapsedTicks));
            Assert.That(snapshot.CurrentRealtimeMargin, Is.GreaterThan(0));
            Assert.That(snapshot.FftwExecutionElapsedTicks, Is.GreaterThan(0));
            Assert.That(snapshot.ChannelProcessingElapsedTicks, Is.GreaterThanOrEqualTo(0));
            Assert.That(snapshot.OutputDeliveryElapsedTicks, Is.GreaterThanOrEqualTo(0));
        });

        if (strategy == ChannelizerStrategy.Fdc)
        {
            Assert.Multiple(() =>
            {
                Assert.That(snapshot.FdcInputCopyBytes, Is.EqualTo(2L * engine.InputRequirements.InputSize * 8));
                Assert.That(snapshot.FdcInputCopyElapsedTicks, Is.GreaterThanOrEqualTo(0));
                Assert.That(snapshot.PfbPolyphaseInputSamples, Is.Zero);
            });
        }
        else
        {
            Assert.Multiple(() =>
            {
                Assert.That(snapshot.PfbPolyphaseInputSamples, Is.EqualTo(2L * engine.InputRequirements.ChunkSize));
                Assert.That(snapshot.PfbPolyphaseElapsedTicks, Is.GreaterThanOrEqualTo(0));
                Assert.That(snapshot.FdcInputCopyBytes, Is.Zero);
            });
        }
    }

    [Test]
    public void DisabledDiagnosticsRemainZero()
    {
        using var engine = ChannelizerFactory.Create(Request(ChannelizerStrategy.Fdc, DiagnosticsMode.Disabled));
        engine.Process(new ComplexF[engine.InputRequirements.InputSize], 0, new ChecksumSink());

        Assert.That(engine.Diagnostics.Snapshot,
            Is.EqualTo(new ChannelizerDiagnosticsSnapshot(
                DiagnosticsMode.Disabled, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                false, ChannelizerFailureKind.None, 0)));
    }

    [TestCase(ChannelizerStrategy.Fdc)]
    [TestCase(ChannelizerStrategy.Pfb)]
    public void SinkFailureStatusRemainsObservableUntilReset(ChannelizerStrategy strategy)
    {
        using var engine = ChannelizerFactory.Create(Request(strategy, DiagnosticsMode.Counters));
        var input = new ComplexF[engine.InputRequirements.InputSize];

        Assert.That(() => engine.Process(input, 0, new ThrowingSink()), Throws.InvalidOperationException);
        var faulted = engine.Diagnostics.Snapshot;
        Assert.Multiple(() =>
        {
            Assert.That(faulted.IsFaulted, Is.True);
            Assert.That(faulted.FailedProcessCount, Is.EqualTo(1));
            Assert.That(faulted.LastFailureKind, Is.EqualTo(ChannelizerFailureKind.OutputSink));
            Assert.That(faulted.ChunksProcessed, Is.Zero);
        });

        engine.Reset(500);
        var reset = engine.Diagnostics.Snapshot;
        Assert.Multiple(() =>
        {
            Assert.That(reset.IsFaulted, Is.False);
            Assert.That(reset.FailedProcessCount, Is.EqualTo(1));
            Assert.That(reset.LastFailureKind, Is.EqualTo(ChannelizerFailureKind.None));
            Assert.That(reset.ReconfigurationCount, Is.EqualTo(1));
        });
    }

    [TestCase(ChannelizerStrategy.Fdc, DiagnosticsMode.Counters)]
    [TestCase(ChannelizerStrategy.Fdc, DiagnosticsMode.StageTiming)]
    [TestCase(ChannelizerStrategy.Pfb, DiagnosticsMode.Counters)]
    [TestCase(ChannelizerStrategy.Pfb, DiagnosticsMode.StageTiming)]
    [NonParallelizable]
    public void EnabledDiagnosticsDoNotAllocateInSteadyState(
        ChannelizerStrategy strategy,
        DiagnosticsMode mode)
    {
        using var engine = ChannelizerFactory.Create(Request(strategy, mode));
        var input = new ComplexF[engine.InputRequirements.InputSize];
        var sink = new ChecksumSink();
        engine.Process(input, 0, sink);
        _ = engine.Diagnostics.Snapshot;
        _ = engine.Diagnostics.GetOutputSamples(11);
        var firstNew = (long)engine.InputRequirements.ChunkSize;
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var iteration = 0; iteration < 20; iteration++)
        {
            engine.Process(input, firstNew, sink);
            _ = engine.Diagnostics.Snapshot;
            _ = engine.Diagnostics.GetOutputSamples(11);
            firstNew += engine.InputRequirements.ChunkSize;
        }

        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        Assert.That(engine.Diagnostics.Snapshot.ChunksProcessed, Is.EqualTo(21));
    }

    private static ChannelizerRequest Request(ChannelizerStrategy strategy, DiagnosticsMode diagnostics) => new(
        1024,
        [
            new ChannelRequest(11, 128, 20, 10),
            new ChannelRequest(22, -128, 20, 10)
        ],
        strategy,
        new InputBlockConstraints(16, 16),
        strategy == ChannelizerStrategy.Fdc
            ? new ChannelizerImplementationHints(
                FdcDecimationFactor: 2,
                Simd: SimdPreference.Scalar,
                Diagnostics: diagnostics)
            : new ChannelizerImplementationHints(
                PfbFftSize: 8,
                PfbHopSize: 4,
                PfbFramesPerBatch: 4,
                Simd: SimdPreference.Scalar,
                Diagnostics: diagnostics));

    private sealed class ChecksumSink : IChannelOutputSink
    {
        public float Checksum { get; private set; }

        public void Write(int channelId, ReadOnlySpan<ComplexF> samples) =>
            Checksum += channelId + samples[0].Real;
    }

    private sealed class ThrowingSink : IChannelOutputSink
    {
        public void Write(int channelId, ReadOnlySpan<ComplexF> samples) =>
            throw new InvalidOperationException("Synthetic diagnostics sink failure.");
    }
}
