using System.Numerics;
using IqChannelizer.Abstractions;
using IqChannelizer.Fdc;
using IqChannelizer.Reference;

namespace IqChannelizer.Tests;

public sealed class PartitionedFdcTests
{
    private const double SampleRate = 1024;
    private const int Chunk = 16;
    private const int Decimation = 2;
    private const double Center = 101.25;

    [Test]
    public void OddOffBinChannelWithBlockerMatchesFullDdcAcrossTwoBlocks()
    {
        var request = Request();
        using var channelizer = ChannelizerFactory.Create(request);
        var engine = (PartitionedFftwFdcEngine)channelizer;
        var history = engine.InputRequirements.HistorySize;
        const long firstNew = (1L << 34) + 37;
        var wholeStart = firstNew - history;
        var whole = DeterministicSignals.Blocker(
            history + (2 * Chunk),
            Center,
            0.7,
            -381.5,
            0.45,
            SampleRate,
            wholeStart);

        var firstSink = new TestSink();
        engine.Process(whole.AsSpan(0, history + Chunk), firstNew, firstSink);
        var transformsAfterInitialization = engine.ForwardTransformExecutionCount;
        var secondSink = new TestSink();
        engine.Process(whole.AsSpan(Chunk, history + Chunk), firstNew + Chunk, secondSink);

        var actual = firstSink.Blocks.Single().Samples
            .Concat(secondSink.Blocks.Single().Samples)
            .ToArray();
        var expected = Reference(whole, wholeStart, request.Channels[0], history);
        Assert.Multiple(() =>
        {
            Assert.That(history, Is.GreaterThanOrEqualTo(2 * Chunk));
            Assert.That(history % Chunk, Is.Not.Zero);
            Assert.That(engine.Plan.Channels[0].CoarseBin & 1, Is.EqualTo(1));
            Assert.That(engine.Plan.Channels[0].ResidualFrequencyHz, Is.Not.Zero);
            Assert.That(transformsAfterInitialization, Is.EqualTo(engine.PartitionCount));
            Assert.That(engine.ForwardTransformExecutionCount,
                Is.EqualTo(transformsAfterInitialization + 1));
            Assert.That(actual, Has.Length.EqualTo(2 * Chunk / Decimation));
        });
        AssertSignals(expected, actual, 8e-4);
    }

    [Test]
    public void ResetRebuildsDelayLineAtNewAbsoluteOrigin()
    {
        var request = Request();
        using var channelizer = ChannelizerFactory.Create(request);
        var engine = (PartitionedFftwFdcEngine)channelizer;
        var history = engine.InputRequirements.HistorySize;
        var initial = DeterministicSignals.Tone(
            history + Chunk,
            Center,
            SampleRate,
            -history,
            0.4);
        engine.Process(initial, 0, new TestSink());
        var transformsBeforeReset = engine.ForwardTransformExecutionCount;

        const long resetFirstNew = (1L << 42) + 11;
        engine.Reset(resetFirstNew);
        var resetStart = resetFirstNew - history;
        var resetInput = DeterministicSignals.Blocker(
            history + Chunk,
            Center,
            0.6,
            409.75,
            0.3,
            SampleRate,
            resetStart);
        var sink = new TestSink();
        engine.Process(resetInput, resetFirstNew, sink);

        var expected = Reference(resetInput, resetStart, request.Channels[0], history);
        AssertSignals(expected, sink.Blocks.Single().Samples, 8e-4);
        Assert.That(engine.ForwardTransformExecutionCount,
            Is.EqualTo(transformsBeforeReset + engine.PartitionCount));
    }

    [Test]
    [NonParallelizable]
    public void ContinuousSteadyStateUsesOneForwardTransformAndAllocatesNothing()
    {
        using var channelizer = ChannelizerFactory.Create(Request());
        var engine = (PartitionedFftwFdcEngine)channelizer;
        var input = new ComplexF[engine.InputRequirements.InputSize];
        var sink = new ChecksumSink();
        engine.Process(input, 0, sink);
        var firstNew = (long)Chunk;
        var transformsBefore = engine.ForwardTransformExecutionCount;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        for (var iteration = 0; iteration < 20; iteration++)
        {
            engine.Process(input, firstNew, sink);
            firstNew += Chunk;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var additionalTransforms = engine.ForwardTransformExecutionCount - transformsBefore;
        Assert.Multiple(() =>
        {
            Assert.That(additionalTransforms, Is.EqualTo(20));
            Assert.That(allocated, Is.Zero);
            Assert.That(sink.BlockCount, Is.EqualTo(21));
        });
    }

    private static ChannelizerRequest Request() => new(
        SampleRate,
        [new ChannelRequest(73, Center, 20, 10, 80, 0.1)],
        ChannelizerStrategy.Fdc,
        new InputBlockConstraints(Chunk, Chunk),
        new ChannelizerImplementationHints(
            FdcDecimationFactor: Decimation,
            Simd: SimdPreference.Scalar));

    private static Complex[] Reference(
        ReadOnlySpan<ComplexF> input,
        long firstInputIndex,
        ChannelRequest channel,
        int history)
    {
        var taps = FdcFilterDesign.DesignAlignedTaps(channel, SampleRate, Decimation);
        if (taps.Length != history + 1)
        {
            var padded = new float[history + 1];
            taps.CopyTo(padded, (history - (taps.Length - 1)) / 2);
            taps = padded;
        }

        return ReferenceDdc.Process(
            input,
            firstInputIndex,
            SampleRate,
            channel.CenterFrequencyHz,
            taps.Select(value => (double)value).ToArray(),
            Decimation).Samples;
    }

    private static void AssertSignals(
        IReadOnlyList<Complex> expected,
        IReadOnlyList<ComplexF> actual,
        double tolerance)
    {
        Assert.That(actual, Has.Count.EqualTo(expected.Count));
        for (var index = 0; index < actual.Count; index++)
        {
            var value = new Complex(actual[index].Real, actual[index].Imaginary);
            Assert.That((value - expected[index]).Magnitude, Is.LessThan(tolerance), $"sample {index}");
        }
    }

    private sealed class ChecksumSink : IChannelOutputSink
    {
        public int BlockCount { get; private set; }
        public float Checksum { get; private set; }

        public void Write(int channelId, ReadOnlySpan<ComplexF> samples)
        {
            BlockCount++;
            Checksum += channelId + samples[0].Real;
        }
    }
}
