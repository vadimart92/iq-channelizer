using System.Numerics;
using System.Text.Json;
using IqChannelizer.Abstractions;
using IqChannelizer.Fdc;
using IqChannelizer.Pfb;
using IqChannelizer.Reference;

namespace IqChannelizer.Tests;

public sealed class SignalValidationAcceptanceTests
{
    private const double SampleRate = 1024;
    private const int Seed = 20260819;

    [Test]
    public void TrackedSummaryDescribesTheExecutableSweepMatrix()
    {
        var repositoryRoot = FindRepositoryRoot();
        var path = Path.Combine(repositoryRoot, "artifacts", "signal-validation", "scalar-acceptance.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var engines = root.GetProperty("engines");

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("schemaVersion").GetInt32(), Is.EqualTo(2));
            Assert.That(root.GetProperty("status").GetString(), Is.EqualTo("passed"));
            Assert.That(root.GetProperty("reproducibility").GetProperty("seed").GetInt32(), Is.EqualTo(Seed));
            Assert.That(engines.GetProperty("fdc").GetProperty("aliasImagesSwept").GetInt32(), Is.EqualTo(7));
            Assert.That(engines.GetProperty("pfb").GetProperty("aliasImagesSwept").GetInt32(), Is.EqualTo(15));
            Assert.That(engines.GetProperty("pfb").GetProperty("prototypeModesSwept").EnumerateArray()
                .Select(value => value.GetString()), Is.EqualTo(new[] { "Conservative", "FoldAware" }));
            Assert.That(root.GetProperty("filters").GetProperty("decimationFactors").EnumerateArray()
                .Select(value => value.GetInt32()), Is.EqualTo(new[] { 2, 4, 8 }));
        });
    }

    [Test]
    public void FdcBlockerSweepCoversEveryAliasImageAndMatchesIndependentDdc()
    {
        const int decimation = 8;
        var request = FdcRequest(decimation);
        var blockerFrequencies = AliasImageFrequencies(decimation);

        Assert.That(blockerFrequencies, Has.Count.EqualTo(decimation - 1));
        foreach (var blockerFrequency in blockerFrequencies)
        {
            using var engine = ChannelizerFactory.Create(request);
            const long firstNew = 20_260_819;
            var frameStart = firstNew - engine.InputRequirements.HistorySize;
            var input = DeterministicSignals.Tone(
                engine.InputRequirements.InputSize,
                blockerFrequency,
                SampleRate,
                frameStart,
                phaseRadians: Seed / 1_000_000d);
            var actual = ProcessSingle(engine, input, firstNew);
            var taps = FdcFilterDesign.DesignAlignedTaps(request.Channels[0], SampleRate, decimation);
            var expected = ReferenceDdc.Process(
                input,
                frameStart,
                SampleRate,
                0,
                taps.Select(value => (double)value).ToArray(),
                decimation).Samples;

            AssertSignals(expected, actual, 2e-4, $"blocker {blockerFrequency:R} Hz");
            Assert.That(Rms(actual), Is.LessThan(0.0032),
                $"seed={Seed}, strategy=Fdc, D={decimation}, blocker={blockerFrequency:R} Hz");
        }
    }

    [TestCase(PfbPrototypeDesignMode.Conservative)]
    [TestCase(PfbPrototypeDesignMode.FoldAware)]
    public void PfbBlockerSweepCoversEveryFinalRateAliasImage(PfbPrototypeDesignMode designMode)
    {
        const int fftSize = 8;
        const int hopSize = 2;
        const int frames = 8;
        var request = PfbRequest(centerFrequencyHz: 0, fftSize, hopSize, frames, designMode);
        var fine = PfbFineStageDesigner.Design(request.Channels[0], SampleRate / hopSize, frames);
        var totalDecimation = hopSize * fine.DecimationFactor;
        var blockerFrequencies = AliasImageFrequencies(totalDecimation);

        Assert.Multiple(() =>
        {
            Assert.That(fine.DecimationFactor, Is.EqualTo(8));
            Assert.That(blockerFrequencies, Has.Count.EqualTo(totalDecimation - 1));
        });

        foreach (var blockerFrequency in blockerFrequencies)
        {
            using var engine = ChannelizerFactory.Create(request);
            var warmupBlocks = 2 + ((fine.Taps.Length - 1 + frames - 1) / frames);
            const long initialFirstNew = 20_260_819;
            ComplexF[]? actual = null;
            for (var block = 0; block < warmupBlocks; block++)
            {
                var firstNew = initialFirstNew + ((long)block * engine.InputRequirements.ChunkSize);
                var input = DeterministicSignals.Tone(
                    engine.InputRequirements.InputSize,
                    blockerFrequency,
                    SampleRate,
                    firstNew - engine.InputRequirements.HistorySize,
                    phaseRadians: Seed / 1_000_000d);
                actual = ProcessSingle(engine, input, firstNew);
            }

            Assert.That(Rms(actual!), Is.LessThan(0.0032),
                $"seed={Seed}, strategy=Pfb, mode={designMode}, K={fftSize}, H={hopSize}, Dfine={fine.DecimationFactor}, blocker={blockerFrequency:R} Hz");
        }
    }

    [TestCase(-64d)]
    [TestCase(64d)]
    public void PfbAcceptsBothWorstCaseHalfBinResiduals(double centerFrequencyHz)
    {
        const int fftSize = 8;
        const int hopSize = 2;
        const int frames = 8;
        var request = PfbRequest(centerFrequencyHz, fftSize, hopSize, frames);
        var fine = PfbFineStageDesigner.Design(request.Channels[0], SampleRate / hopSize, frames);
        using var engine = ChannelizerFactory.Create(request);
        var warmupBlocks = 2 + ((fine.Taps.Length - 1 + frames - 1) / frames);
        const long initialFirstNew = 31_415_926;
        ComplexF[]? actual = null;

        for (var block = 0; block < warmupBlocks; block++)
        {
            var firstNew = initialFirstNew + ((long)block * engine.InputRequirements.ChunkSize);
            var input = DeterministicSignals.Tone(
                engine.InputRequirements.InputSize,
                centerFrequencyHz,
                SampleRate,
                firstNew - engine.InputRequirements.HistorySize,
                amplitude: 0.75,
                phaseRadians: -0.37);
            actual = ProcessSingle(engine, input, firstNew);
        }

        Assert.Multiple(() =>
        {
            Assert.That(Math.Abs(engine.Plan.Channels.Single().ResidualFrequencyHz), Is.EqualTo(64).Within(1e-12));
            Assert.That(actual, Is.Not.Null.And.Length.EqualTo(1));
            Assert.That(actual!.Single().Magnitude, Is.EqualTo(0.75).Within(6e-4));
        });
    }

    [TestCase(2)]
    [TestCase(4)]
    [TestCase(8)]
    public void FdcStandaloneAndFoldedResponseSweepMeetsSpecification(int decimation)
    {
        var request = FdcRequest(decimation);
        var channel = request.Channels[0];
        var taps = FdcFilterDesign.DesignAlignedTaps(channel, SampleRate, decimation);
        var spec = new IqChannelizer.Dsp.LowPassFilterSpec(
            SampleRate,
            channel.PassbandWidthHz / 2,
            (channel.PassbandWidthHz + channel.TransitionWidthHz) / 2,
            channel.PassbandRippleDb,
            channel.StopbandAttenuationDb);
        var standalone = IqChannelizer.Dsp.FrequencyResponseEvaluator.MeasureLowPass(taps, spec, 4097);
        var dense = IqChannelizer.Dsp.FrequencyResponseEvaluator.EvaluateDenseConservative(taps, SampleRate, 4097);
        var folded = IqChannelizer.Dsp.AliasedResponseEvaluator.EvaluateConservative(
            dense,
            decimation,
            channel.PassbandWidthHz / 2,
            1025);

        Assert.Multiple(() =>
        {
            Assert.That(standalone.PassbandRippleDb, Is.LessThanOrEqualTo(channel.PassbandRippleDb));
            Assert.That(standalone.StopbandAttenuationDb, Is.GreaterThanOrEqualTo(channel.StopbandAttenuationDb));
            Assert.That(folded.AliasImageCount, Is.EqualTo(decimation - 1));
            Assert.That(folded.WorstAliasAttenuationDb, Is.GreaterThanOrEqualTo(channel.StopbandAttenuationDb));
        });
    }

    private static ChannelizerRequest FdcRequest(int decimation) => new(
        SampleRate,
        [new ChannelRequest(101, 0, 20, 20, 50, 0.2)],
        ChannelizerStrategy.Fdc,
        new InputBlockConstraints(128, 128),
        new ChannelizerImplementationHints(FdcDecimationFactor: decimation, Simd: SimdPreference.Scalar));

    private static ChannelizerRequest PfbRequest(
        double centerFrequencyHz,
        int fftSize,
        int hopSize,
        int frames,
        PfbPrototypeDesignMode designMode = PfbPrototypeDesignMode.Conservative) => new(
        SampleRate,
        [new ChannelRequest(202, centerFrequencyHz, 20, 20, 50, 0.2)],
        ChannelizerStrategy.Pfb,
        new InputBlockConstraints(frames * hopSize, frames * hopSize),
        new ChannelizerImplementationHints(
            PfbFftSize: fftSize,
            PfbHopSize: hopSize,
            PfbFramesPerBatch: frames,
            Simd: SimdPreference.Scalar,
            PfbPrototypeDesign: designMode));

    private static IReadOnlyList<double> AliasImageFrequencies(int decimation)
    {
        var outputRate = SampleRate / decimation;
        var result = new double[decimation - 1];
        for (var image = 1; image < decimation; image++)
        {
            var frequency = image * outputRate;
            result[image - 1] = frequency > SampleRate / 2 ? frequency - SampleRate : frequency;
        }

        return result;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "implementation-plan.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException(
            "Could not locate the repository root containing implementation-plan.md.");
    }

    private static ComplexF[] ProcessSingle(IStreamingChannelizer engine, ComplexF[] input, long firstNew)
    {
        var sink = new TestSink();
        engine.Process(input, firstNew, sink);
        Assert.That(sink.Blocks, Has.Count.EqualTo(1));
        Assert.That(sink.Blocks.Single().Samples, Has.Length.EqualTo(engine.Plan.Channels.Single().OutputSamplesPerProcess));
        return sink.Blocks.Single().Samples;
    }

    private static double Rms(IReadOnlyList<ComplexF> samples) =>
        Math.Sqrt(samples.Average(sample => sample.Magnitude * sample.Magnitude));

    private static void AssertSignals(
        IReadOnlyList<Complex> expected,
        IReadOnlyList<ComplexF> actual,
        double tolerance,
        string configuration)
    {
        Assert.That(actual, Has.Count.EqualTo(expected.Count), configuration);
        for (var index = 0; index < actual.Count; index++)
        {
            var error = new Complex(actual[index].Real, actual[index].Imaginary) - expected[index];
            Assert.That(error.Magnitude, Is.LessThan(tolerance), $"{configuration}, sample={index}");
        }
    }
}
