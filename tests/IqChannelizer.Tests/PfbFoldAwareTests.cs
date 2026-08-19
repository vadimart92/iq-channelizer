using IqChannelizer.Abstractions;
using IqChannelizer.Pfb;
using IqChannelizer.Reference;

namespace IqChannelizer.Tests;

public sealed class PfbFoldAwareTests
{
    [Test]
    public void FoldAwarePrototypeIsShorterAndStillMeetsConservativeFoldedSpec()
    {
        var request = Request(PfbPrototypeDesignMode.Conservative);
        var conservative = PfbPrototypeDesign.Design(request, fftSize: 8, hopSize: 2);
        var foldAware = PfbPrototypeDesign.Design(
            request,
            fftSize: 8,
            hopSize: 2,
            PfbPrototypeDesignMode.FoldAware);

        Assert.Multiple(() =>
        {
            Assert.That(foldAware.DesignMode, Is.EqualTo(PfbPrototypeDesignMode.FoldAware));
            Assert.That(foldAware.Taps.Length, Is.LessThan(conservative.Taps.Length));
            Assert.That(foldAware.AliasedResponse.WorstAliasAttenuationDb, Is.GreaterThanOrEqualTo(50));
            Assert.That(foldAware.Taps.Length % 8, Is.Zero);
        });
    }

    [Test]
    public void ExplicitFoldAwarePlanReportsModeAndPreservesPassbandAmplitude()
    {
        var request = Request(PfbPrototypeDesignMode.FoldAware);
        using var engine = ChannelizerFactory.Create(request);
        ComplexF[]? actual = null;
        const long initialFirstNew = 4_000_003;
        for (var block = 0; block < 20; block++)
        {
            var firstNew = initialFirstNew + ((long)block * engine.InputRequirements.ChunkSize);
            var input = DeterministicSignals.Tone(
                engine.InputRequirements.InputSize,
                0,
                1024,
                firstNew - engine.InputRequirements.HistorySize,
                0.75,
                -0.31);
            var sink = new TestSink();
            engine.Process(input, firstNew, sink);
            actual = sink.Blocks.Single().Samples;
        }

        Assert.Multiple(() =>
        {
            Assert.That(engine.Plan.FilterDesignMode, Is.EqualTo("KaiserFoldAware"));
            Assert.That(engine.Plan.Channels.Single().PrototypeFilterId, Does.StartWith("KaiserFoldAwarePfb"));
            Assert.That(actual, Is.Not.Null.And.Not.Empty);
            var minimumAllowed = 0.75 * Math.Pow(10, -0.2 / 20);
            Assert.That(actual![^1].Magnitude, Is.InRange(minimumAllowed, 0.75 * 1.001));
        });
    }

    private static ChannelizerRequest Request(PfbPrototypeDesignMode mode) => new(
        1024,
        [new ChannelRequest(1, 0, 20, 20, 50, 0.2)],
        ChannelizerStrategy.Pfb,
        new InputBlockConstraints(16, 16),
        new ChannelizerImplementationHints(
            PfbFftSize: 8,
            PfbHopSize: 2,
            PfbFramesPerBatch: 8,
            Simd: SimdPreference.Scalar,
            PfbPrototypeDesign: mode));
}
