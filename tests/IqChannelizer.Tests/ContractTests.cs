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
    public void ForcedAvx2IsSelectedWhenSupportedAndRejectedOtherwise()
    {
        var request = Request(ChannelizerStrategy.Fdc, [Channel(1, 0)]) with
        {
            Hints = new ChannelizerImplementationHints(Simd: SimdPreference.Avx2)
        };
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported && System.Runtime.Intrinsics.X86.Fma.IsSupported)
        {
            using var engine = ChannelizerFactory.Create(request);
            Assert.That(engine.Plan.SelectedSimdBackend, Is.EqualTo(SimdPreference.Avx2));
        }
        else
        {
            Assert.That(() => ChannelizerFactory.Create(request), Throws.TypeOf<PlatformNotSupportedException>());
        }
    }

    [Test]
    public void ForcedAvx512IsSelectedWhenSupportedAndRejectedOtherwise()
    {
        var request = Request(ChannelizerStrategy.Fdc, [Channel(1, 0)]) with
        {
            Hints = new ChannelizerImplementationHints(Simd: SimdPreference.Avx512)
        };
        if (System.Runtime.Intrinsics.X86.Avx512F.IsSupported)
        {
            using var engine = ChannelizerFactory.Create(request);
            Assert.That(engine.Plan.SelectedSimdBackend, Is.EqualTo(SimdPreference.Avx512));
        }
        else
        {
            Assert.That(() => ChannelizerFactory.Create(request), Throws.TypeOf<PlatformNotSupportedException>());
        }
    }

    [Test]
    public void UnknownDiagnosticsModeIsRejected()
    {
        var request = Request(ChannelizerStrategy.Fdc, [Channel(1, 0)]) with
        {
            Hints = new ChannelizerImplementationHints(
                Simd: SimdPreference.Scalar,
                Diagnostics: (DiagnosticsMode)999)
        };

        Assert.That(() => ChannelizerFactory.Create(request), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void UnknownPfbPrototypeDesignModeIsRejected()
    {
        var request = Request(ChannelizerStrategy.Pfb, [Channel(1, 0)]) with
        {
            Hints = new ChannelizerImplementationHints(
                Simd: SimdPreference.Scalar,
                PfbPrototypeDesign: (PfbPrototypeDesignMode)999)
        };

        Assert.That(() => ChannelizerFactory.Create(request), Throws.TypeOf<ArgumentOutOfRangeException>());
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
            Assert.That(resolved.FineDecimationFactor, Is.EqualTo(4));
            Assert.That(resolved.OutputSamplesPerProcess, Is.EqualTo(1));
            Assert.That(resolved.InputSamplesPerOutputSample, Is.EqualTo(new RationalSampleOffset(12, 1)));
        });
    }

    [TestCase(ChannelizerStrategy.Fdc)]
    [TestCase(ChannelizerStrategy.Pfb)]
    public void ResolvedPlanCollectionsAreImmutableSnapshots(ChannelizerStrategy strategy)
    {
        var requestedChannels = new List<ChannelRequest> { Channel(17, 128) };
        var request = Request(strategy, requestedChannels) with
        {
            InputBlocks = new InputBlockConstraints(16, 16),
            Hints = strategy == ChannelizerStrategy.Fdc
                ? new ChannelizerImplementationHints(FdcDecimationFactor: 2, Simd: SimdPreference.Scalar)
                : new ChannelizerImplementationHints(
                    PfbFftSize: 8,
                    PfbHopSize: 4,
                    PfbFramesPerBatch: 4,
                    Simd: SimdPreference.Scalar)
        };

        using var engine = ChannelizerFactory.Create(request);
        requestedChannels[0] = Channel(99, -128);

        var resolvedChannels = (IList<ResolvedChannelPlan>)engine.Plan.Channels;
        var warnings = (IList<string>)engine.Plan.Warnings;

        Assert.Multiple(() =>
        {
            Assert.That(engine.Plan.Channels.Single().ChannelId, Is.EqualTo(17));
            Assert.That(() => resolvedChannels[0] = resolvedChannels[0], Throws.TypeOf<NotSupportedException>());
            Assert.That(() => warnings.Add("mutation"), Throws.TypeOf<NotSupportedException>());
        });
    }

    [TestCase(ChannelizerStrategy.Fdc)]
    [TestCase(ChannelizerStrategy.Pfb)]
    public void NegativeNyquistUsesTheCanonicalSignedBin(ChannelizerStrategy strategy)
    {
        var request = Request(strategy, [Channel(1, -512)]) with
        {
            InputBlocks = new InputBlockConstraints(16, 16),
            Hints = strategy == ChannelizerStrategy.Fdc
                ? new ChannelizerImplementationHints(FdcDecimationFactor: 2, Simd: SimdPreference.Scalar)
                : new ChannelizerImplementationHints(PfbFftSize: 8, PfbHopSize: 4, PfbFramesPerBatch: 4, Simd: SimdPreference.Scalar)
        };

        using var engine = ChannelizerFactory.Create(request);
        var channel = engine.Plan.Channels.Single();

        Assert.Multiple(() =>
        {
            Assert.That(channel.CoarseCenterFrequencyHz, Is.EqualTo(-512));
            Assert.That(channel.ResidualFrequencyHz, Is.Zero);
            Assert.That(channel.CoarseBin, Is.EqualTo(engine.Plan.FftSize!.Value / 2));
        });
    }

    [TestCase(ChannelizerStrategy.Fdc)]
    [TestCase(ChannelizerStrategy.Pfb)]
    public void PositiveNyquistNeighbourUsesWrappedResidual(ChannelizerStrategy strategy)
    {
        var channelRequest = new ChannelRequest(1, 511, 400, 600, 50, 0.2);
        var request = Request(strategy, [channelRequest]) with
        {
            InputBlocks = new InputBlockConstraints(16, 16),
            Hints = strategy == ChannelizerStrategy.Fdc
                ? new ChannelizerImplementationHints(FdcDecimationFactor: 1, Simd: SimdPreference.Scalar)
                : new ChannelizerImplementationHints(
                    PfbFftSize: 8,
                    PfbHopSize: 1,
                    PfbFramesPerBatch: 16,
                    Simd: SimdPreference.Scalar)
        };

        using var engine = ChannelizerFactory.Create(request);
        var channel = engine.Plan.Channels.Single();

        Assert.Multiple(() =>
        {
            Assert.That(channel.CoarseCenterFrequencyHz, Is.EqualTo(-512));
            Assert.That(channel.ResidualFrequencyHz, Is.EqualTo(-1));
        });
    }

    internal static ChannelizerRequest Request(ChannelizerStrategy strategy, IReadOnlyList<ChannelRequest> channels) =>
        new(1024, channels, strategy, new InputBlockConstraints(16, 32));

    internal static ChannelRequest Channel(int id, double center) => new(id, center, 20, 10);
}
