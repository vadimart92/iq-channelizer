using IqChannelizer.Abstractions;

namespace IqChannelizer.Tests;

public sealed class StreamingFlowTests
{
    [Test]
    public void FdcRoutesOneDeterministicBlockPerChannelAndPreservesTone()
    {
        var request = ContractTests.Request(ChannelizerStrategy.Fdc, [ContractTests.Channel(11, 128), ContractTests.Channel(22, -128)]) with
        {
            Hints = new ChannelizerImplementationHints(FdcDecimationFactor: 2, Simd: SimdPreference.Scalar)
        };
        using var engine = ChannelizerFactory.Create(request);
        var input = Tone(engine.InputRequirements.InputSize, 128, request.InputSampleRateHz, 0);
        var sink = new TestSink();

        engine.Process(input, 0, sink);

        Assert.That(sink.Blocks.Select(x => x.ChannelId), Is.EqualTo(new[] { 11, 22 }));
        Assert.That(sink.Blocks.All(x => x.Samples.Length == 8), Is.True);
        Assert.That(sink.Blocks[0].Samples.All(x => Math.Abs(x.Magnitude - 1) < 1e-4), Is.True);
    }

    [TestCase(8, 8)]
    [TestCase(8, 4)]
    [TestCase(8, 3)]
    public void PfbSupportsCriticalHalfAndArbitraryIntegerHop(int fftSize, int hop)
    {
        var request = ContractTests.Request(ChannelizerStrategy.Pfb, [ContractTests.Channel(9, 128)]) with
        {
            InputBlocks = new InputBlockConstraints(hop * 3, hop * 3),
            Hints = new ChannelizerImplementationHints(PfbFftSize: fftSize, PfbHopSize: hop, PfbFramesPerBatch: 3, Simd: SimdPreference.Scalar)
        };
        using var engine = ChannelizerFactory.Create(request);
        var firstNew = 19L;
        var absoluteStart = firstNew - engine.InputRequirements.HistorySize;
        var input = Tone(engine.InputRequirements.InputSize, 128, request.InputSampleRateHz, absoluteStart);
        var sink = new TestSink();

        engine.Process(input, firstNew, sink);

        Assert.That(sink.Blocks, Has.Count.EqualTo(1));
        Assert.That(sink.Blocks[0].Samples, Has.Length.EqualTo(3));
        Assert.That(sink.Blocks[0].Samples.All(x => Math.Abs(x.Magnitude - 1) < 1e-4), Is.True);
        Assert.That(sink.Blocks[0].Samples.All(x => Math.Abs(x.Imaginary) < 1e-4), Is.True);
    }

    [Test]
    public void ResidualMixerUsesAbsoluteOriginAndOutputStride()
    {
        const double frequency = 22;
        const double sampleRate = 1024;
        const long firstAbsoluteIndex = 11;
        const int stride = 4;
        var samples = new ComplexF[12];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = ComplexF.FromPolar(
                2 * Math.PI * frequency * (firstAbsoluteIndex + (index * stride)) / sampleRate);
        }

        IqChannelizer.Dsp.ScalarRotator.RotateInPlace(samples, frequency, sampleRate, firstAbsoluteIndex, stride);

        Assert.That(samples.All(x => Math.Abs(x.Real - 1) < 2e-5), Is.True);
        Assert.That(samples.All(x => Math.Abs(x.Imaginary) < 2e-5), Is.True);
    }

    [Test]
    public void ProcessEnforcesExactLengthAndContinuity()
    {
        using var engine = ChannelizerFactory.Create(ContractTests.Request(ChannelizerStrategy.Fdc, [ContractTests.Channel(1, 0)]));
        var input = new ComplexF[engine.InputRequirements.InputSize];
        var sink = new TestSink();

        Assert.That(() => engine.Process(input.AsSpan(1), 0, sink), Throws.ArgumentException);
        engine.Process(input, 0, sink);
        Assert.That(() => engine.Process(input, engine.InputRequirements.ChunkSize + 1, sink), Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void PfbContinuesAcrossProcessBoundaries()
    {
        var request = ContractTests.Request(ChannelizerStrategy.Pfb, [ContractTests.Channel(5, 128)]) with
        {
            InputBlocks = new InputBlockConstraints(8, 8),
            Hints = new ChannelizerImplementationHints(PfbFftSize: 8, PfbHopSize: 4, PfbFramesPerBatch: 2, Simd: SimdPreference.Scalar)
        };
        using var engine = ChannelizerFactory.Create(request);
        var sink = new TestSink();
        var first = 13L;
        var firstInput = Tone(engine.InputRequirements.InputSize, 128, 1024, first - engine.InputRequirements.HistorySize);
        engine.Process(firstInput, first, sink);
        var secondFirst = first + engine.InputRequirements.ChunkSize;
        var secondInput = Tone(engine.InputRequirements.InputSize, 128, 1024, secondFirst - engine.InputRequirements.HistorySize);
        engine.Process(secondInput, secondFirst, sink);

        Assert.That(sink.Blocks, Has.Count.EqualTo(2));
        Assert.That(sink.Blocks.SelectMany(x => x.Samples).All(x => Math.Abs(x.Real - 1) < 1e-4 && Math.Abs(x.Imaginary) < 1e-4), Is.True);
    }

    private static ComplexF[] Tone(int count, double frequency, double sampleRate, long absoluteStart)
    {
        var result = new ComplexF[count];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = ComplexF.FromPolar(2 * Math.PI * frequency * (absoluteStart + index) / sampleRate);
        }

        return result;
    }
}
