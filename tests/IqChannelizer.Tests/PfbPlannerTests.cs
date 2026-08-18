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
            Assert.That(engine.Plan.FftSize, Is.EqualTo(64));
            Assert.That(engine.Plan.HopSize, Is.EqualTo(33));
            Assert.That(engine.Plan.Channels.Single().CoarseOutputSampleRateHz,
                Is.GreaterThanOrEqualTo(30));
            Assert.That(engine.InputRequirements.ChunkSize, Is.EqualTo(132));
            Assert.That(finalSink!.Blocks.Single().Samples
                .All(sample => Math.Abs(sample.Magnitude - 0.75) < 4e-4), Is.True);
        });
    }

    private static ChannelizerRequest AutomaticRequest() => new(
        SampleRate,
        [new ChannelRequest(1, 123, 20, 10, 50, 0.2)],
        ChannelizerStrategy.Pfb,
        new InputBlockConstraints(116, 116),
        new ChannelizerImplementationHints(Simd: SimdPreference.Scalar));
}
