using System.Numerics;
using IqChannelizer.Abstractions;
using IqChannelizer.Pfb;
using IqChannelizer.Reference;

namespace IqChannelizer.Tests;

public sealed class PfbArbitraryPlacementTests
{
    private const double SampleRate = 1024;
    private const int FftSize = 8;

    [Test]
    public void ArbitraryPositiveNegativeAndNyquistWrappedBinsMatchIndependentEffectiveDdc()
    {
        const int hopSize = 2;
        const int frames = 16;
        var channels = new[]
        {
            ArbitraryChannel(11, 139.5),
            ArbitraryChannel(22, -273.25),
            ArbitraryChannel(33, 509.75)
        };
        var request = Request(channels, hopSize, frames);
        var prototype = PfbPrototypeDesign.Design(request, FftSize, hopSize);
        var fineStages = channels
            .Select(channel => PfbFineStageDesigner.Design(channel, SampleRate / hopSize, frames))
            .ToArray();
        using var engine = ChannelizerFactory.Create(request);
        var warmupBlocks = 1 + fineStages.Max(stage => DivideRoundUp(stage.Taps.Length - 1, frames));
        const long initialFirstNew = 1_000_003;
        TestSink? finalSink = null;
        long finalFirstNew = 0;

        for (var block = 0; block < warmupBlocks; block++)
        {
            finalFirstNew = initialFirstNew + ((long)block * engine.InputRequirements.ChunkSize);
            var spanStart = finalFirstNew - engine.InputRequirements.HistorySize;
            finalSink = new TestSink();
            engine.Process(
                DeterministicComplexSignal(engine.InputRequirements.InputSize, spanStart),
                finalFirstNew,
                finalSink);
        }

        Assert.That(finalSink, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(engine.Plan.Channels.Select(channel => channel.CoarseBin),
                Is.EqualTo(new[] { 1, 6, 4 }));
            Assert.That(engine.Plan.Channels.Select(channel => channel.CoarseCenterFrequencyHz),
                Is.EqualTo(new[] { 128d, -256d, -512d }));
            Assert.That(engine.Plan.Channels.Select(channel => channel.ResidualFrequencyHz),
                Is.EqualTo(new[] { 11.5, -17.25, -2.25 }).Within(1e-12));
            Assert.That(engine.Plan.Channels.Select(channel => channel.CoarseBin).Distinct().Count(),
                Is.EqualTo(3));
            Assert.That(engine.Plan.Channels.Select(channel => channel.FineDecimationFactor),
                Is.All.EqualTo(4));
            Assert.That(finalSink!.Blocks.Select(block => block.ChannelId),
                Is.EqualTo(new[] { 11, 22, 33 }));
        });

        for (var channelIndex = 0; channelIndex < channels.Length; channelIndex++)
        {
            var expected = EffectiveReference(
                channels[channelIndex],
                engine.Plan.Channels[channelIndex],
                prototype.Taps,
                fineStages[channelIndex],
                hopSize,
                frames,
                finalFirstNew);
            AssertSignals(expected, finalSink!.Blocks[channelIndex].Samples, 4e-4,
                $"channel {channels[channelIndex].ChannelId}");
        }
    }

    [Test]
    public void CriticallySampledAlignedBinsMatchReferenceAcrossBatchPartitions()
    {
        const int hopSize = FftSize;
        const int smallFrames = 4;
        const int largeFrames = 8;
        const long firstNew = 2_000_003;
        var channels = new[]
        {
            AlignedChannel(41, 0),
            AlignedChannel(42, 128),
            AlignedChannel(43, -256),
            AlignedChannel(44, -512)
        };
        var smallRequest = Request(channels, hopSize, smallFrames);
        var largeRequest = Request(channels, hopSize, largeFrames);
        var prototype = PfbPrototypeDesign.Design(largeRequest, FftSize, hopSize);
        using var smallEngine = ChannelizerFactory.Create(smallRequest);
        using var largeEngine = ChannelizerFactory.Create(largeRequest);

        var small = ProcessBlocks(smallEngine, firstNew, blockCount: 2);
        var large = ProcessBlocks(largeEngine, firstNew, blockCount: 1);

        Assert.Multiple(() =>
        {
            Assert.That(smallEngine.Plan.OversamplingRatio, Is.EqualTo(new RationalSampleOffset(1, 1)));
            Assert.That(largeEngine.Plan.OversamplingRatio, Is.EqualTo(new RationalSampleOffset(1, 1)));
            Assert.That(smallEngine.Plan.Channels.Select(channel => channel.ResidualFrequencyHz), Is.All.Zero);
            Assert.That(largeEngine.Plan.Channels.Select(channel => channel.ResidualFrequencyHz), Is.All.Zero);
            Assert.That(smallEngine.Plan.Channels.Select(channel => channel.FineDecimationFactor), Is.All.EqualTo(1));
            Assert.That(largeEngine.Plan.Channels.Select(channel => channel.FineFilterId), Is.All.EqualTo("Identity"));
        });

        foreach (var channel in channels)
        {
            var expected = AlignedReference(channel, prototype.Taps, firstNew, largeFrames);
            AssertSignals(expected, small[channel.ChannelId], 3e-4,
                $"small partition, channel {channel.ChannelId}");
            AssertSignals(expected, large[channel.ChannelId], 3e-4,
                $"large partition, channel {channel.ChannelId}");
            AssertSignals(
                small[channel.ChannelId].Select(ToComplex).ToArray(),
                large[channel.ChannelId],
                3e-5,
                $"partition equivalence, channel {channel.ChannelId}");
        }
    }

    private static ChannelRequest ArbitraryChannel(int channelId, double centerFrequencyHz) => new(
        channelId,
        centerFrequencyHz,
        PassbandWidthHz: 32,
        TransitionWidthHz: 64,
        StopbandAttenuationDb: 40,
        PassbandRippleDb: 0.3,
        PreferredOutputSampleRateHz: 128);

    private static ChannelRequest AlignedChannel(int channelId, double centerFrequencyHz) => new(
        channelId,
        centerFrequencyHz,
        PassbandWidthHz: 96,
        TransitionWidthHz: 32,
        StopbandAttenuationDb: 40,
        PassbandRippleDb: 0.3,
        PreferredOutputSampleRateHz: 128);

    private static ChannelizerRequest Request(
        IReadOnlyList<ChannelRequest> channels,
        int hopSize,
        int frames) => new(
        SampleRate,
        channels,
        ChannelizerStrategy.Pfb,
        new InputBlockConstraints(frames * hopSize, frames * hopSize),
        new ChannelizerImplementationHints(
            PfbFftSize: FftSize,
            PfbHopSize: hopSize,
            PfbFramesPerBatch: frames,
            Simd: SimdPreference.Scalar));

    private static Dictionary<int, ComplexF[]> ProcessBlocks(
        IStreamingChannelizer engine,
        long firstNew,
        int blockCount)
    {
        var samples = engine.Plan.Channels.ToDictionary(
            channel => channel.ChannelId,
            _ => new List<ComplexF>());
        for (var block = 0; block < blockCount; block++)
        {
            var blockFirstNew = firstNew + ((long)block * engine.InputRequirements.ChunkSize);
            var spanStart = blockFirstNew - engine.InputRequirements.HistorySize;
            var sink = new TestSink();
            engine.Process(
                DeterministicComplexSignal(engine.InputRequirements.InputSize, spanStart),
                blockFirstNew,
                sink);
            foreach (var output in sink.Blocks)
            {
                samples[output.ChannelId].AddRange(output.Samples);
            }
        }

        return samples.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }

    private static Complex[] EffectiveReference(
        ChannelRequest requested,
        ResolvedChannelPlan resolved,
        float[] prototype,
        PfbFineStageDesign fine,
        int hopSize,
        int frames,
        long firstNew)
    {
        var effective = ConvolveAtHop(prototype, fine.Taps, hopSize, resolved.ResidualFrequencyHz);
        var outputCount = frames / fine.DecimationFactor;
        var firstAnchor = firstNew + hopSize - 1;
        var referenceStart = firstAnchor - (effective.Length - 1);
        var input = DeterministicComplexSignal(
            effective.Length + ((outputCount - 1) * hopSize * fine.DecimationFactor),
            referenceStart);
        return ReferenceDdc.ProcessComplexTaps(
            input,
            referenceStart,
            SampleRate,
            requested.CenterFrequencyHz,
            effective,
            hopSize * fine.DecimationFactor).Samples;
    }

    private static Complex[] AlignedReference(
        ChannelRequest channel,
        float[] prototype,
        long firstNew,
        int frames)
    {
        var firstAnchor = firstNew + FftSize - 1;
        var referenceStart = firstAnchor - (prototype.Length - 1);
        var input = DeterministicComplexSignal(
            prototype.Length + ((frames - 1) * FftSize),
            referenceStart);
        return ReferenceDdc.Process(
            input,
            referenceStart,
            SampleRate,
            channel.CenterFrequencyHz,
            prototype.Select(value => (double)value).ToArray(),
            FftSize).Samples;
    }

    private static Complex[] ConvolveAtHop(
        IReadOnlyList<float> prototype,
        IReadOnlyList<float> fine,
        int hopSize,
        double residualFrequencyHz)
    {
        var result = new Complex[prototype.Count + ((fine.Count - 1) * hopSize)];
        for (var fineIndex = 0; fineIndex < fine.Count; fineIndex++)
        {
            for (var prototypeIndex = 0; prototypeIndex < prototype.Count; prototypeIndex++)
            {
                var residualPhase = -2 * Math.PI * residualFrequencyHz * prototypeIndex / SampleRate;
                result[prototypeIndex + (fineIndex * hopSize)] +=
                    Complex.FromPolarCoordinates(fine[fineIndex] * prototype[prototypeIndex], residualPhase);
            }
        }

        return result;
    }

    private static ComplexF[] DeterministicComplexSignal(int count, long firstSampleIndex)
    {
        double[] frequencies = [139.5, -273.25, 509.75, 37.25];
        double[] amplitudes = [0.31, 0.27, 0.23, 0.19];
        double[] phases = [0.17, -0.41, 0.73, -1.07];
        var result = new ComplexF[count];
        for (var index = 0; index < count; index++)
        {
            var absoluteIndex = checked(firstSampleIndex + index);
            var sample = Complex.Zero;
            for (var tone = 0; tone < frequencies.Length; tone++)
            {
                var phase = phases[tone] + (2 * Math.PI * frequencies[tone] * absoluteIndex / SampleRate);
                sample += Complex.FromPolarCoordinates(amplitudes[tone], phase);
            }

            result[index] = new ComplexF((float)sample.Real, (float)sample.Imaginary);
        }

        return result;
    }

    private static int DivideRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;

    private static Complex ToComplex(ComplexF value) => new(value.Real, value.Imaginary);

    private static void AssertSignals(
        IReadOnlyList<Complex> expected,
        IReadOnlyList<ComplexF> actual,
        double tolerance,
        string context)
    {
        Assert.That(actual, Has.Count.EqualTo(expected.Count), context);
        for (var index = 0; index < actual.Count; index++)
        {
            var error = ToComplex(actual[index]) - expected[index];
            Assert.That(error.Magnitude, Is.LessThan(tolerance),
                $"{context}, sample {index}, expected={expected[index]}, actual={actual[index]}");
        }
    }
}
