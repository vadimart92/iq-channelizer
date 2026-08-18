using System.Numerics;
using IqChannelizer.Abstractions;
using IqChannelizer.Fdc;
using IqChannelizer.Reference;

namespace IqChannelizer.Tests;

public sealed class FdcOverlapSaveTests
{
    private const double SampleRate = 1024;
    private const int Decimation = 4;

    [TestCase(128.0, 128.0)]
    [TestCase(-128.0, -128.0)]
    [TestCase(123.25, 123.25)]
    [TestCase(480.0, 480.0)]
    public void MatchesIndependentDdcForPositiveNegativeResidualAndWrappedCenters(
        double channelCenter,
        double toneFrequency)
    {
        var request = Request(channelCenter);
        using var engine = ChannelizerFactory.Create(request);
        const long firstNew = 317;
        var frameStart = firstNew - engine.InputRequirements.HistorySize;
        var input = DeterministicSignals.Tone(
            engine.InputRequirements.InputSize,
            toneFrequency,
            SampleRate,
            frameStart,
            amplitude: 0.7,
            phaseRadians: 0.31);

        var actual = Process(engine, input, firstNew);
        var expected = Reference(input, frameStart, request.Channels[0], engine.InputRequirements.HistorySize);

        AssertSignals(expected, actual, 3e-4);
        Assert.That(actual.All(sample => Math.Abs(sample.Magnitude - 0.7) < 3e-4), Is.True);
    }

    [Test]
    public void DiscardsExactlyHistoryAndContinuesAcrossProcessBoundaries()
    {
        var request = Request(123.25);
        using var engine = ChannelizerFactory.Create(request);
        const long firstNew = 1000;
        var history = engine.InputRequirements.HistorySize;
        var chunk = engine.InputRequirements.ChunkSize;
        var wholeStart = firstNew - history;
        var whole = DeterministicSignals.Tone(history + (2 * chunk), 123.25, SampleRate, wholeStart, 0.8, -0.2);
        var first = whole.AsSpan(0, history + chunk).ToArray();
        var second = whole.AsSpan(chunk, history + chunk).ToArray();

        var firstActual = Process(engine, first, firstNew);
        var secondActual = Process(engine, second, firstNew + chunk);
        var actual = firstActual.Concat(secondActual).ToArray();
        var expected = Reference(whole, wholeStart, request.Channels[0], history);

        Assert.That(firstActual, Has.Length.EqualTo(chunk / Decimation));
        Assert.That(secondActual, Has.Length.EqualTo(chunk / Decimation));
        AssertSignals(expected, actual, 4e-4);
    }

    [Test]
    public void RejectsAnAliasingBlockerAndMatchesReferenceDdc()
    {
        var request = Request(0);
        using var engine = ChannelizerFactory.Create(request);
        const long firstNew = 71;
        var frameStart = firstNew - engine.InputRequirements.HistorySize;
        var input = DeterministicSignals.Tone(engine.InputRequirements.InputSize, 300, SampleRate, frameStart);

        var actual = Process(engine, input, firstNew);
        var expected = Reference(input, frameStart, request.Channels[0], engine.InputRequirements.HistorySize);

        AssertSignals(expected, actual, 2e-4);
        var rms = Math.Sqrt(actual.Average(sample => sample.Magnitude * sample.Magnitude));
        Assert.That(rms, Is.LessThan(0.0015));
    }

    [Test]
    public void PlanUsesFullFrameAndExactOverlapSaveDivisibility()
    {
        using var engine = ChannelizerFactory.Create(Request(123.25));
        var plan = engine.Plan.Channels.Single();

        Assert.Multiple(() =>
        {
            Assert.That(engine.InputRequirements.HistorySize, Is.GreaterThan(0));
            Assert.That(engine.InputRequirements.HistorySize % Decimation, Is.Zero);
            Assert.That(engine.InputRequirements.ChunkSize, Is.EqualTo(64));
            Assert.That(engine.InputRequirements.ChunkSize, Is.LessThanOrEqualTo(64));
            Assert.That(engine.Plan.FftSize, Is.EqualTo(engine.InputRequirements.InputSize));
            Assert.That(plan.ShortInverseFftLength, Is.EqualTo(engine.InputRequirements.InputSize / Decimation));
            Assert.That(plan.OutputSamplesPerProcess, Is.EqualTo(64 / Decimation));
            Assert.That(plan.GroupDelayInputSamples,
                Is.EqualTo(new RationalSampleOffset(engine.InputRequirements.HistorySize, 2)));
        });
    }

    private static ChannelizerRequest Request(double center) => new(
        SampleRate,
        [new ChannelRequest(17, center, 80, 80, 60, 0.2)],
        ChannelizerStrategy.Fdc,
        new InputBlockConstraints(64, 64),
        new ChannelizerImplementationHints(FdcDecimationFactor: Decimation, Simd: SimdPreference.Scalar));

    private static ComplexF[] Process(IStreamingChannelizer engine, ComplexF[] input, long firstNew)
    {
        var sink = new TestSink();
        engine.Process(input, firstNew, sink);
        return sink.Blocks.Single().Samples;
    }

    private static Complex[] Reference(
        ComplexF[] input,
        long frameStart,
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
            frameStart,
            SampleRate,
            channel.CenterFrequencyHz,
            taps.Select(value => (double)value).ToArray(),
            Decimation).Samples;
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
