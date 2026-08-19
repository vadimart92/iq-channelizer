using System.Numerics;
using IqChannelizer.Abstractions;
using IqChannelizer.Fdc;
using IqChannelizer.Reference;

namespace IqChannelizer.Tests;

public sealed class FdcPlannerTests
{
    private const double SampleRate = 1024;

    [Test]
    public void SelectsMultipleDecimationsSharesForwardFftAndMatchesIndependentDdc()
    {
        var channels = new[]
        {
            new ChannelRequest(101, 123.25, 40, 40, 60, 0.2),
            new ChannelRequest(202, -170, 180, 120, 60, 0.2)
        };
        var request = new ChannelizerRequest(
            SampleRate,
            channels,
            ChannelizerStrategy.Fdc,
            new InputBlockConstraints(64, 64),
            new ChannelizerImplementationHints(Simd: SimdPreference.Scalar));
        using var channelizer = ChannelizerFactory.Create(request);
        var engine = channelizer;
        var history = engine.InputRequirements.HistorySize;
        const long firstNew = 509;
        var frameStart = firstNew - history;
        var input = DeterministicSignals.TwoTone(
            engine.InputRequirements.InputSize,
            123.25,
            0.7,
            -170,
            0.35,
            SampleRate,
            frameStart);
        var sink = new TestSink();

        engine.Process(input, firstNew, sink);

        Assert.Multiple(() =>
        {
            Assert.That(engine.Plan.Channels.Select(channel => channel.DecimationFactor), Is.EqualTo(new[] { 8, 2 }));
            Assert.That(engine.Plan.Channels.Select(channel => channel.OutputSamplesPerProcess), Is.EqualTo(new[] { 8, 32 }));
            Assert.That(engine.Plan.Channels.Select(channel => channel.ShortInverseFftLength),
                Is.EqualTo(new int?[] { engine.Plan.FftSize / 8, engine.Plan.FftSize / 2 }));
            Assert.That(engine.Plan.ChunkAlignment, Is.EqualTo(8));
            Assert.That(history % 8, Is.Zero);
            Assert.That(engine, Is.TypeOf<PartitionedFftwFdcEngine>());
            Assert.That(((PartitionedFftwFdcEngine)engine).InverseGroupCount, Is.EqualTo(2));
            Assert.That(((PartitionedFftwFdcEngine)engine).ForwardTransformExecutionCount,
                Is.EqualTo(((PartitionedFftwFdcEngine)engine).PartitionCount));
            Assert.That(sink.Blocks.Select(block => block.ChannelId), Is.EqualTo(new[] { 101, 202 }));
        });

        for (var channelIndex = 0; channelIndex < channels.Length; channelIndex++)
        {
            var channel = channels[channelIndex];
            var decimation = engine.Plan.Channels[channelIndex].DecimationFactor;
            var taps = PadToHistory(
                FdcFilterDesign.DesignAlignedTaps(channel, SampleRate, decimation),
                history);
            var expected = ReferenceDdc.Process(
                input,
                frameStart,
                SampleRate,
                channel.CenterFrequencyHz,
                taps.Select(value => (double)value).ToArray(),
                decimation).Samples;
            AssertSignals(expected, sink.Blocks[channelIndex].Samples, 6e-4);
        }
    }

    [Test]
    public void PreferredRateConstrainsAutomaticDecimationWhileForcedHintOverridesPlanner()
    {
        var channel = new ChannelRequest(1, 0, 40, 40, 60, 0.2, PreferredOutputSampleRateHz: 200);
        var automatic = new ChannelizerRequest(
            SampleRate,
            [channel],
            ChannelizerStrategy.Fdc,
            new InputBlockConstraints(64, 64));
        var forced = automatic with
        {
            Hints = new ChannelizerImplementationHints(FdcDecimationFactor: 2, Simd: SimdPreference.Scalar)
        };

        using var automaticEngine = ChannelizerFactory.Create(automatic);
        using var forcedEngine = ChannelizerFactory.Create(forced);

        Assert.Multiple(() =>
        {
            Assert.That(automaticEngine.Plan.Channels.Single().DecimationFactor, Is.EqualTo(4));
            Assert.That(automaticEngine.Plan.Channels.Single().OutputSampleRateHz, Is.EqualTo(256));
            Assert.That(forcedEngine.Plan.Channels.Single().DecimationFactor, Is.EqualTo(2));
            Assert.That(forcedEngine.Plan.Channels.Single().OutputSampleRateHz, Is.EqualTo(512));
        });
    }

    [TestCase(840, true)]
    [TestCase(1024, true)]
    [TestCase(121, false)]
    public void SmoothTransformClassifierUsesFftwFriendlyFactors(int length, bool expected) =>
        Assert.That(FdcPlanner.IsSmoothTransformLength(length), Is.EqualTo(expected));

    private static float[] PadToHistory(float[] taps, int history)
    {
        if (taps.Length == history + 1)
        {
            return taps;
        }

        var result = new float[history + 1];
        taps.CopyTo(result, (history - (taps.Length - 1)) / 2);
        return result;
    }

    private static void AssertSignals(Complex[] expected, ComplexF[] actual, double tolerance)
    {
        Assert.That(actual, Has.Length.EqualTo(expected.Length));
        for (var index = 0; index < actual.Length; index++)
        {
            var error = new Complex(actual[index].Real, actual[index].Imaginary) - expected[index];
            Assert.That(error.Magnitude, Is.LessThan(tolerance), $"sample {index}");
        }
    }
}
