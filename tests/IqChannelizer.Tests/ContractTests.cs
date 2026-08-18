using System.Runtime.CompilerServices;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Tests;

public sealed class ContractTests
{
    [Test]
    public void ComplexFHasFftwCompatibleSize() => Assert.That(Unsafe.SizeOf<ComplexF>(), Is.EqualTo(8));

    [Test]
    public void RationalOffsetsAreNormalized()
    {
        var value = new RationalSampleOffset(-6, -8);
        Assert.That(value, Is.EqualTo(new RationalSampleOffset(3, 4)));
    }

    [TestCase(-1, 1)]
    [TestCase(0, 0)]
    [TestCase(int.MaxValue, 1)]
    public void InvalidInputRequirementsAreRejected(int history, int chunk) =>
        Assert.That(() => _ = new InputRequirements(history, chunk), Throws.InstanceOf<ArgumentException>());

    [Test]
    public void DuplicateChannelIdsAreRejected()
    {
        var channels = new[] { Channel(7, 0), Channel(7, 100) };
        Assert.That(() => ChannelizerFactory.Create(Request(ChannelizerStrategy.Fdc, channels)), Throws.ArgumentException);
    }

    [Test]
    public void AutoIsExplicitlyUnsupported()
    {
        Assert.That(() => ChannelizerFactory.Create(Request(ChannelizerStrategy.Auto, [Channel(1, 0)])), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void ForcedSimdIsRejectedForScalarFoundation()
    {
        var request = Request(ChannelizerStrategy.Fdc, [Channel(1, 0)]) with
        {
            Hints = new ChannelizerImplementationHints(Simd: SimdPreference.Avx2)
        };
        Assert.That(() => ChannelizerFactory.Create(request), Throws.TypeOf<NotSupportedException>());
    }

    [TestCase(double.NaN, 80, 0.1, null, null)]
    [TestCase(20, double.NaN, 0.1, null, null)]
    [TestCase(20, 0, 0.1, null, null)]
    [TestCase(20, 80, double.PositiveInfinity, null, null)]
    [TestCase(20, 80, 0, null, null)]
    [TestCase(20, 80, 0.1, 0, null)]
    [TestCase(20, 80, 0.1, null, double.NaN)]
    public void InvalidChannelNumericFieldsAreRejected(
        double passband,
        double attenuation,
        double ripple,
        double? minimumRate,
        double? preferredRate)
    {
        var channel = new ChannelRequest(1, 0, passband, 10, attenuation, ripple, minimumRate, preferredRate);
        Assert.That(() => ChannelizerFactory.Create(Request(ChannelizerStrategy.Fdc, [channel])),
            Throws.InstanceOf<ArgumentException>());
    }

    [Test]
    public void PreferredRateCannotBeBelowMinimumRate()
    {
        var channel = Channel(1, 0) with { MinimumOutputSampleRateHz = 200, PreferredOutputSampleRateHz = 100 };
        Assert.That(() => ChannelizerFactory.Create(Request(ChannelizerStrategy.Fdc, [channel])), Throws.ArgumentException);
    }

    [TestCase(0, null, null, null)]
    [TestCase(3, null, null, null)]
    [TestCase(null, 1, null, null)]
    [TestCase(null, null, 0, null)]
    [TestCase(null, null, null, 0)]
    [TestCase(null, 8, 9, 1)]
    public void InvalidForcedHintsAreRejected(int? fdcD, int? pfbK, int? pfbH, int? frames)
    {
        var request = Request(ChannelizerStrategy.Fdc, [Channel(1, 0)]) with
        {
            Hints = new ChannelizerImplementationHints(fdcD, pfbK, pfbH, frames, SimdPreference.Scalar)
        };
        Assert.That(() => ChannelizerFactory.Create(request), Throws.InstanceOf<ArgumentException>());
    }

    [Test]
    public void ResolvedPlanContainsExactFdcContractMetadata()
    {
        var channel = new ChannelRequest(91, 128, 20, 10, 75, 0.2, 100, 600);
        var request = Request(ChannelizerStrategy.Fdc, [channel]) with
        {
            Hints = new ChannelizerImplementationHints(FdcDecimationFactor: 2, Simd: SimdPreference.Scalar)
        };

        using var engine = ChannelizerFactory.Create(request);
        var resolved = engine.Plan.Channels.Single();

        Assert.Multiple(() =>
        {
            Assert.That(engine.Plan.ChunkAlignment, Is.EqualTo(2));
            Assert.That(engine.Plan.SelectedSimdBackend, Is.EqualTo(SimdPreference.Scalar));
            Assert.That(engine.Plan.FftwThreadCount, Is.EqualTo(1));
            Assert.That(engine.Plan.AlignedBufferBytes, Is.GreaterThan(0));
            Assert.That(resolved.ChannelId, Is.EqualTo(91));
            Assert.That(resolved.PassbandWidthHz, Is.EqualTo(20));
            Assert.That(resolved.StopbandAttenuationDb, Is.EqualTo(75));
            Assert.That(engine.InputRequirements.HistorySize, Is.GreaterThan(0));
            Assert.That(engine.InputRequirements.HistorySize % 2, Is.Zero);
            Assert.That(engine.Plan.FftSize, Is.EqualTo(engine.InputRequirements.InputSize));
            Assert.That(resolved.ShortInverseFftLength, Is.EqualTo(engine.InputRequirements.InputSize / 2));
            Assert.That(resolved.OutputSamplesPerProcess, Is.EqualTo(8));
            Assert.That(resolved.FirstOutputInputSampleOffset,
                Is.EqualTo(new RationalSampleOffset(-engine.InputRequirements.HistorySize, 2)));
            Assert.That(resolved.GroupDelayInputSamples,
                Is.EqualTo(new RationalSampleOffset(engine.InputRequirements.HistorySize, 2)));
            Assert.That(resolved.PrototypeFilterId, Does.StartWith("KaiserFdcOrder"));
            Assert.That(resolved.Warning, Does.Contain("below the preferred rate"));
        });
    }

    [Test]
    public void ResolvedPlanContainsExactPfbTimingAndRatio()
    {
        var request = Request(ChannelizerStrategy.Pfb, [Channel(-17, 128)]) with
        {
            InputBlocks = new InputBlockConstraints(12, 12),
            Hints = new ChannelizerImplementationHints(PfbFftSize: 8, PfbHopSize: 3, PfbFramesPerBatch: 4, Simd: SimdPreference.Scalar)
        };

        using var engine = ChannelizerFactory.Create(request);
        var resolved = engine.Plan.Channels.Single();

        Assert.Multiple(() =>
        {
            Assert.That(engine.Plan.OversamplingRatio, Is.EqualTo(new RationalSampleOffset(8, 3)));
            Assert.That(engine.Plan.PfbPhaseShiftMode, Is.EqualTo("PreFftCircularShift"));
            Assert.That(engine.Plan.TapsPerPhase, Is.GreaterThan(1));
            Assert.That(engine.InputRequirements.HistorySize,
                Is.EqualTo((engine.Plan.TapsPerPhase!.Value * 8) - 1));
            Assert.That(resolved.PfbGroupId, Is.EqualTo(0));
            Assert.That(resolved.PfbFftSize, Is.EqualTo(8));
            Assert.That(resolved.PfbHopSize, Is.EqualTo(3));
            Assert.That(resolved.GroupDelayInputSamples.Numerator, Is.GreaterThan(0));
            Assert.That(resolved.FirstOutputInputSampleOffset,
                Is.EqualTo(new RationalSampleOffset(
                    2 - resolved.GroupDelayInputSamples.Numerator,
                    resolved.GroupDelayInputSamples.Denominator)));
            Assert.That(resolved.InputSamplesPerOutputSample, Is.EqualTo(new RationalSampleOffset(3, 1)));
        });
    }

    internal static ChannelizerRequest Request(ChannelizerStrategy strategy, IReadOnlyList<ChannelRequest> channels) =>
        new(1024, channels, strategy, new InputBlockConstraints(16, 32));

    internal static ChannelRequest Channel(int id, double center) => new(id, center, 20, 10);
}
