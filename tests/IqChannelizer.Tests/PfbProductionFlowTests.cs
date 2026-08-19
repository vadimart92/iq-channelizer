using System.Numerics;
using IqChannelizer.Abstractions;
using IqChannelizer.Pfb;
using IqChannelizer.Reference;

namespace IqChannelizer.Tests;

public sealed class PfbProductionFlowTests
{
    private const double SampleRate = 1024;
    private const int FftSize = 8;
    private const int HopSize = 2;
    private const int Frames = 8;

    [Test]
    public void AOneTapGainIsNotMisclassifiedAsTheIdentityRoute()
    {
        var design = new PfbFineStageDesign(
            1,
            [0.5f],
            new RationalSampleOffset(0, 1),
            default);
        var decimator = new StreamingFineDecimator(design, inputCount: 2);
        ComplexF[] input = [new(2, -4), new(6, 8)];
        var output = new ComplexF[2];

        decimator.Process(input, output);

        Assert.Multiple(() =>
        {
            Assert.That(design.FilterId, Is.EqualTo("KaiserFineD1Order0"));
            Assert.That(output, Is.EqualTo(new ComplexF[] { new(1, -2), new(3, 4) }));
        });
    }

    [Test]
    public void SharedBinFanOutAndDifferentFineDecimationsMatchIndependentDdc()
    {
        var request = Request();
        var prototype = PfbPrototypeDesign.Design(request, FftSize, HopSize);
        var fineStages = request.Channels
            .Select(channel => PfbFineStageDesigner.Design(channel, SampleRate / HopSize, Frames))
            .ToArray();
        using var channelizer = ChannelizerFactory.Create(request);
        var engine = (FftwPfbEngine)channelizer;
        var warmupBlocks = 2 + fineStages.Max(stage => (stage.Taps.Length - 1 + Frames - 1) / Frames);
        const long initialFirstNew = 2000;
        TestSink? finalSink = null;
        long finalFirstNew = 0;

        for (var block = 0; block < warmupBlocks; block++)
        {
            finalFirstNew = initialFirstNew + ((long)block * engine.InputRequirements.ChunkSize);
            var spanStart = finalFirstNew - engine.InputRequirements.HistorySize;
            var input = DeterministicSignals.TwoTone(
                engine.InputRequirements.InputSize,
                123.25,
                0.7,
                120,
                0.35,
                SampleRate,
                spanStart);
            finalSink = new TestSink();
            engine.Process(input, finalFirstNew, finalSink);
        }

        Assert.That(finalSink, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(engine.UniqueBinCount, Is.EqualTo(1));
            Assert.That(engine.Plan.Channels.Select(channel => channel.CoarseBin), Is.EqualTo(new[] { 1, 1 }));
            Assert.That(engine.Plan.Channels.Select(channel => channel.FineDecimationFactor), Is.EqualTo(new[] { 8, 2 }));
            Assert.That(engine.Plan.Channels.Select(channel => channel.OutputSamplesPerProcess), Is.EqualTo(new[] { 1, 4 }));
            Assert.That(finalSink!.Blocks.Select(block => block.ChannelId), Is.EqualTo(new[] { 11, 22 }));
            Assert.That(engine.GatheredBinValueCount, Is.EqualTo((long)warmupBlocks * Frames));
        });

        for (var channelIndex = 0; channelIndex < request.Channels.Count; channelIndex++)
        {
            var channel = request.Channels[channelIndex];
            var fine = fineStages[channelIndex];
            var residual = channel.CenterFrequencyHz -
                           engine.Plan.Channels[channelIndex].CoarseCenterFrequencyHz;
            var effective = ConvolveAtHop(prototype.Taps, fine.Taps, HopSize, residual);
            var outputCount = Frames / fine.DecimationFactor;
            var firstAnchor = finalFirstNew + HopSize - 1;
            var referenceStart = firstAnchor - (effective.Length - 1);
            var referenceInput = DeterministicSignals.TwoTone(
                effective.Length + ((outputCount - 1) * HopSize * fine.DecimationFactor),
                123.25,
                0.7,
                120,
                0.35,
                SampleRate,
                referenceStart);
            var expected = ReferenceDdc.ProcessComplexTaps(
                referenceInput,
                referenceStart,
                SampleRate,
                channel.CenterFrequencyHz,
                effective,
                HopSize * fine.DecimationFactor).Samples;

            AssertSignals(expected, finalSink!.Blocks[channelIndex].Samples, 1.5e-3);
        }
    }

    [Test]
    public void ResetClearsFineFilterHistory()
    {
        var request = Request() with { Channels = [Request().Channels[0]] };
        using var engine = ChannelizerFactory.Create(request);
        const long firstNew = 100;
        var input = DeterministicSignals.Tone(
            engine.InputRequirements.InputSize,
            123.25,
            SampleRate,
            firstNew - engine.InputRequirements.HistorySize);
        engine.Process(input, firstNew, new TestSink());

        engine.Reset(900);
        var sink = new TestSink();
        engine.Process(new ComplexF[engine.InputRequirements.InputSize], 900, sink);

        Assert.That(sink.Blocks.Single().Samples.All(sample => sample.Magnitude == 0), Is.True);
    }

    [Test]
    public void FineFactorOneStillAppliesTheRequestedPerChannelFilter()
    {
        var request = new ChannelizerRequest(
            SampleRate,
            [
                new ChannelRequest(1, 100, 20, 10, 80, 0.1, PreferredOutputSampleRateHz: 400),
                new ChannelRequest(2, 128, 300, 50, 80, 0.1)
            ],
            ChannelizerStrategy.Pfb,
            new InputBlockConstraints(16, 16),
            new ChannelizerImplementationHints(
                PfbFftSize: FftSize,
                PfbHopSize: HopSize,
                PfbFramesPerBatch: Frames,
                Simd: SimdPreference.Scalar));
        var fine = PfbFineStageDesigner.Design(request.Channels[0], SampleRate / HopSize, Frames);
        using var engine = ChannelizerFactory.Create(request);
        var warmupBlocks = 2 + ((fine.Taps.Length - 1 + Frames - 1) / Frames);
        const long initialFirstNew = 10_000;
        TestSink? finalSink = null;

        for (var block = 0; block < warmupBlocks; block++)
        {
            var firstNew = initialFirstNew + ((long)block * engine.InputRequirements.ChunkSize);
            var input = DeterministicSignals.Tone(
                engine.InputRequirements.InputSize,
                180,
                SampleRate,
                firstNew - engine.InputRequirements.HistorySize);
            finalSink = new TestSink();
            engine.Process(input, firstNew, finalSink);
        }

        var resolved = engine.Plan.Channels[0];
        var samples = finalSink!.Blocks[0].Samples;
        var rms = Math.Sqrt(samples.Average(sample => sample.Magnitude * sample.Magnitude));
        Assert.Multiple(() =>
        {
            Assert.That(resolved.FineDecimationFactor, Is.EqualTo(1));
            Assert.That(resolved.FineFilterId, Does.StartWith("KaiserFineD1"));
            Assert.That(rms, Is.LessThan(2e-4));
        });
    }

    [Test]
    public void FramesPerBatchDoesNotChangeLogicalOutput()
    {
        var channel = new ChannelRequest(7, 123.25, 120, 80, 50, 0.2);
        var smallRequest = PartitionRequest(channel, framesPerBatch: 4);
        var largeRequest = PartitionRequest(channel, framesPerBatch: 8);

        var smallBlocks = ProcessContinuousTone(smallRequest, blockCount: 12, firstNewSampleIndex: 20_000);
        var largeBlocks = ProcessContinuousTone(largeRequest, blockCount: 6, firstNewSampleIndex: 20_000);

        Assert.That(smallBlocks, Has.Length.EqualTo(largeBlocks.Length));
        for (var index = 0; index < smallBlocks.Length; index++)
        {
            var error = new Complex(
                smallBlocks[index].Real - largeBlocks[index].Real,
                smallBlocks[index].Imaginary - largeBlocks[index].Imaginary);
            Assert.That(error.Magnitude, Is.LessThan(2e-5), $"sample {index}");
        }
    }

    private static ChannelizerRequest Request() => new(
        SampleRate,
        [
            new ChannelRequest(11, 123.25, 20, 20, 50, 0.2),
            new ChannelRequest(22, 120, 120, 80, 50, 0.2)
        ],
        ChannelizerStrategy.Pfb,
        new InputBlockConstraints(Frames * HopSize, Frames * HopSize),
        new ChannelizerImplementationHints(
            PfbFftSize: FftSize,
            PfbHopSize: HopSize,
            PfbFramesPerBatch: Frames,
            Simd: SimdPreference.Scalar));

    private static ChannelizerRequest PartitionRequest(ChannelRequest channel, int framesPerBatch) => new(
        SampleRate,
        [channel],
        ChannelizerStrategy.Pfb,
        new InputBlockConstraints(framesPerBatch * HopSize, framesPerBatch * HopSize),
        new ChannelizerImplementationHints(
            PfbFftSize: FftSize,
            PfbHopSize: HopSize,
            PfbFramesPerBatch: framesPerBatch,
            Simd: SimdPreference.Scalar));

    private static ComplexF[] ProcessContinuousTone(
        ChannelizerRequest request,
        int blockCount,
        long firstNewSampleIndex)
    {
        using var engine = ChannelizerFactory.Create(request);
        var result = new List<ComplexF>();
        for (var block = 0; block < blockCount; block++)
        {
            var firstNew = firstNewSampleIndex + ((long)block * engine.InputRequirements.ChunkSize);
            var input = DeterministicSignals.Tone(
                engine.InputRequirements.InputSize,
                123.25,
                SampleRate,
                firstNew - engine.InputRequirements.HistorySize);
            var sink = new TestSink();
            engine.Process(input, firstNew, sink);
            result.AddRange(sink.Blocks.Single().Samples);
        }

        return result.ToArray();
    }

    private static Complex[] ConvolveAtHop(float[] prototype, float[] fine, int hop, double residualFrequencyHz)
    {
        var result = new Complex[prototype.Length + ((fine.Length - 1) * hop)];
        for (var fineIndex = 0; fineIndex < fine.Length; fineIndex++)
        {
            for (var prototypeIndex = 0; prototypeIndex < prototype.Length; prototypeIndex++)
            {
                var residualPhase = -2 * Math.PI * residualFrequencyHz * prototypeIndex / SampleRate;
                result[prototypeIndex + (fineIndex * hop)] +=
                    Complex.FromPolarCoordinates(fine[fineIndex] * prototype[prototypeIndex], residualPhase);
            }
        }

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
