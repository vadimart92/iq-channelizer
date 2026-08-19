using IqChannelizer.Abstractions;
using IqChannelizer.Pfb;
using IqChannelizer.Reference;

namespace IqChannelizer.Tests;

public sealed class PfbPlannerTests
{
    private const double SampleRate = 1024;

    [Test]
    public void AutomaticPlannerSelectsFeasibleArbitraryHopAndExactChunkShape()
    {
        var request = AutomaticRequest();

        using var engine = ChannelizerFactory.Create(request);

        Assert.Multiple(() =>
        {
            Assert.That(engine.Plan.FftSize, Is.EqualTo(64));
            Assert.That(engine.Plan.HopSize, Is.EqualTo(29));
            Assert.That(engine.Plan.HopSize, Is.Not.EqualTo(engine.Plan.FftSize));
            Assert.That(engine.Plan.HopSize, Is.Not.EqualTo(engine.Plan.FftSize / 2));
            Assert.That(engine.Plan.FramesPerBatch, Is.EqualTo(4));
            Assert.That(engine.InputRequirements.ChunkSize, Is.EqualTo(116));
            Assert.That(engine.Plan.OversamplingRatio, Is.EqualTo(new RationalSampleOffset(64, 29)));
            Assert.That(engine.Plan.BenchmarkProfileKey, Is.Null);
            Assert.That(engine.Plan.Warnings, Has.Some.Contains("deterministic feasibility policy"));
            Assert.That(engine.Plan.Channels.Single().FineFilterId, Does.StartWith("KaiserFineD1"));
        });
    }

    [Test]
    public void CandidateOrderingIsDeterministic()
    {
        var request = AutomaticRequest();

        var first = PfbPlanner.InspectCandidates(request);
        var repeated = PfbPlanner.InspectCandidates(request);

        Assert.That(repeated, Is.EqualTo(first));
        Assert.That(first[0], Is.EqualTo((64, 29, 4)));
    }

    [Test]
    public void ForcedShapeThatCannotFitTheBlockConstraintIsRejectedClearly()
    {
        var request = AutomaticRequest() with
        {
            InputBlocks = new InputBlockConstraints(16, 16),
            Hints = new ChannelizerImplementationHints(
                PfbFftSize: 64,
                PfbHopSize: 29,
                PfbFramesPerBatch: 1,
                Simd: SimdPreference.Scalar)
        };

        Assert.That(
            () => ChannelizerFactory.Create(request),
            Throws.ArgumentException.With.Message.Contains("No PFB K/H/FramesPerBatch candidate"));
    }

    [Test]
    public void AutomaticPlannerSearchesBelowTheLargestGeometryFeasibleHToFitSmallBlocks()
    {
        var request = AutomaticRequest() with
        {
            InputBlocks = new InputBlockConstraints(16, 16)
        };

        using var engine = ChannelizerFactory.Create(request);

        Assert.Multiple(() =>
        {
            Assert.That(engine.Plan.FftSize, Is.EqualTo(64));
            Assert.That(engine.Plan.HopSize, Is.EqualTo(16));
            Assert.That(engine.Plan.FramesPerBatch, Is.EqualTo(1));
            Assert.That(engine.InputRequirements.ChunkSize, Is.EqualTo(16));
        });
    }

    [Test]
    public void AutomaticPlannerNeverSelectsACoarseRateBelowTheOccupiedWidth()
    {
        var request = new ChannelizerRequest(
            SampleRate,
            [new ChannelRequest(1, 128, 20, 10, 50, 0.2)],
            ChannelizerStrategy.Pfb,
            new InputBlockConstraints(132, 132),
            new ChannelizerImplementationHints(Simd: SimdPreference.Scalar));

        using var engine = ChannelizerFactory.Create(request);
        var frames = engine.Plan.FramesPerBatch!.Value;
        var fine = PfbFineStageDesigner.Design(
            request.Channels[0],
            engine.Plan.Channels.Single().CoarseOutputSampleRateHz,
            frames);
        var warmupBlocks = 2 + ((fine.Taps.Length - 1 + frames - 1) / frames);
        const long initialFirstNew = 10_000;
        TestSink? finalSink = null;
        for (var block = 0; block < warmupBlocks; block++)
        {
            var firstNew = initialFirstNew + ((long)block * engine.InputRequirements.ChunkSize);
            var input = DeterministicSignals.Tone(
                engine.InputRequirements.InputSize,
                128,
                SampleRate,
                firstNew - engine.InputRequirements.HistorySize,
                0.75,
                0.3);
            finalSink = new TestSink();
            engine.Process(input, firstNew, finalSink);
        }

        Assert.Multiple(() =>
        {
            Assert.That(engine.Plan.FftSize, Is.EqualTo(32));
            Assert.That(engine.Plan.HopSize, Is.EqualTo(32));
            Assert.That(engine.Plan.Channels.Single().CoarseOutputSampleRateHz,
                Is.GreaterThanOrEqualTo(30));
            Assert.That(engine.InputRequirements.ChunkSize, Is.EqualTo(128));
            Assert.That(finalSink!.Blocks.Single().Samples
                .All(sample => Math.Abs(sample.Magnitude - 0.75) < 4e-4), Is.True);
        });
    }

    [Test]
    public void ForcedFramesDoNotHideAFeasibleLowerHopBehindTheHopShortlist()
    {
        var request = new ChannelizerRequest(
            SampleRate,
            [new ChannelRequest(1, 128, 20, 10, 50, 0.2)],
            ChannelizerStrategy.Pfb,
            new InputBlockConstraints(128, 128),
            new ChannelizerImplementationHints(
                PfbFftSize: 64,
                PfbFramesPerBatch: 8,
                Simd: SimdPreference.Scalar));

        using var engine = ChannelizerFactory.Create(request);

        Assert.Multiple(() =>
        {
            Assert.That(engine.Plan.FftSize, Is.EqualTo(64));
            Assert.That(engine.Plan.HopSize, Is.EqualTo(16));
            Assert.That(engine.Plan.FramesPerBatch, Is.EqualTo(8));
            Assert.That(engine.InputRequirements.ChunkSize, Is.EqualTo(128));
        });
    }

    [Test]
    public void AutomaticPlannerUsesCriticalPrototypeOnlyPathForBinAlignedChannels()
    {
        var request = new ChannelizerRequest(
            SampleRate,
            [
                new ChannelRequest(1, 0, 20, 10, 50, 0.2),
                new ChannelRequest(2, 128, 20, 10, 50, 0.2),
                new ChannelRequest(3, -256, 20, 10, 50, 0.2)
            ],
            ChannelizerStrategy.Pfb,
            new InputBlockConstraints(128, 128),
            new ChannelizerImplementationHints(Simd: SimdPreference.Scalar));

        using var channelizer = ChannelizerFactory.Create(request);
        var engine = (FftwPfbEngine)channelizer;

        Assert.Multiple(() =>
        {
            Assert.That(engine.Plan.FftSize, Is.EqualTo(32));
            Assert.That(engine.Plan.HopSize, Is.EqualTo(32));
            Assert.That(engine.Plan.FramesPerBatch, Is.EqualTo(4));
            Assert.That(engine.Plan.OversamplingRatio, Is.EqualTo(new RationalSampleOffset(1, 1)));
            Assert.That(engine.Plan.Channels.Select(channel => channel.FineFilterId),
                Is.All.EqualTo("Identity"));
            Assert.That(engine.PrototypeOnlyChannelCount, Is.EqualTo(3));
            Assert.That(engine.RotationChannelCount, Is.Zero);
        });
    }

    [Test]
    public void CriticalTargetRequiresTheForcedFrameBatchToFit()
    {
        var request = new ChannelizerRequest(
            SampleRate,
            [new ChannelRequest(1, 128, 20, 10, 50, 0.2)],
            ChannelizerStrategy.Pfb,
            new InputBlockConstraints(128, 128),
            new ChannelizerImplementationHints(
                PfbFramesPerBatch: 8,
                Simd: SimdPreference.Scalar));

        using var engine = ChannelizerFactory.Create(request);

        Assert.Multiple(() =>
        {
            Assert.That(engine.Plan.FftSize, Is.EqualTo(64));
            Assert.That(engine.Plan.HopSize, Is.EqualTo(16));
            Assert.That(engine.Plan.FramesPerBatch, Is.EqualTo(8));
            Assert.That(engine.Plan.OversamplingRatio, Is.EqualTo(new RationalSampleOffset(4, 1)));
            Assert.That(engine.Plan.Channels.Single().FineFilterId, Does.StartWith("KaiserFineD"));
        });
    }

    [Test]
    public void CriticalPrototypeOnlyPathStillEnforcesTheRequestedStopband()
    {
        var request = new ChannelizerRequest(
            SampleRate,
            [new ChannelRequest(7, 128, 20, 10, 50, 0.2)],
            ChannelizerStrategy.Pfb,
            new InputBlockConstraints(128, 128),
            new ChannelizerImplementationHints(Simd: SimdPreference.Scalar));

        var wantedRms = ProcessToneRms(request, 128);
        var blockerRms = ProcessToneRms(request, 148);

        Assert.Multiple(() =>
        {
            Assert.That(wantedRms, Is.InRange(0.97, 1.01));
            Assert.That(blockerRms, Is.LessThan(0.004));
        });
    }

    [Test]
    public void FoldAwareCriticalPlanRetainsItsPerChannelFineFilter()
    {
        var request = new ChannelizerRequest(
            SampleRate,
            [new ChannelRequest(9, 128, 20, 10, 50, 0.2)],
            ChannelizerStrategy.Pfb,
            new InputBlockConstraints(128, 128),
            new ChannelizerImplementationHints(
                PfbFftSize: 32,
                PfbHopSize: 32,
                PfbFramesPerBatch: 4,
                Simd: SimdPreference.Scalar,
                PfbPrototypeDesign: PfbPrototypeDesignMode.FoldAware));

        using var channelizer = ChannelizerFactory.Create(request);
        var engine = (FftwPfbEngine)channelizer;

        Assert.Multiple(() =>
        {
            Assert.That(engine.Plan.Channels.Single().FineFilterId, Does.StartWith("KaiserFineD1"));
            Assert.That(engine.PrototypeOnlyChannelCount, Is.Zero);
            Assert.That(engine.RotationChannelCount, Is.Zero);
        });
    }

    private static double ProcessToneRms(ChannelizerRequest request, double frequencyHz)
    {
        using var engine = ChannelizerFactory.Create(request);
        const long firstNew = 10_003;
        var input = DeterministicSignals.Tone(
            engine.InputRequirements.InputSize,
            frequencyHz,
            request.InputSampleRateHz,
            firstNew - engine.InputRequirements.HistorySize);
        var sink = new TestSink();
        engine.Process(input, firstNew, sink);
        var samples = sink.Blocks.Single().Samples;
        return Math.Sqrt(samples.Average(sample => sample.Magnitude * sample.Magnitude));
    }

    private static ChannelizerRequest AutomaticRequest() => new(
        SampleRate,
        [new ChannelRequest(1, 123, 20, 10, 50, 0.2)],
        ChannelizerStrategy.Pfb,
        new InputBlockConstraints(116, 116),
        new ChannelizerImplementationHints(Simd: SimdPreference.Scalar));
}
