using System.Text.Json;
using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;
using IqChannelizer.Pfb;

namespace IqChannelizer.Benchmarks;

internal static class PrototypeStudyRunner
{
    private const double InputSampleRateHz = 100_000_000;
    private const int ChannelCount = 100;
    private const double ChannelSpacingHz = 500_000;
    private const int FftSize = 4_096;
    private const int HopSize = 1_871;
    private const int DenseResponsePoints = 16_385;
    private const int FoldedResponsePoints = 1_025;

    public static void Run(string[] args)
    {
        var outputPath = ValueAfter(args, "--output") ??
                         Path.Combine("artifacts", "uprof", "prototype-study.json");
        var request = Request();
        var requirements = PfbPrototypeDesign.Analyze(request, FftSize, HopSize);
        var candidates = new List<CandidateResult>();

        foreach (var tapsPerPhase in new[] { 8, 12, 16, 21 })
        {
            var tapCount = checked(tapsPerPhase * FftSize);
            var transitionRadians = 2 * Math.PI *
                                    ((requirements.StopbandEdgeHz - requirements.PassbandEdgeHz) /
                                     InputSampleRateHz);
            var impliedAttenuationDb = 8 + (2.285 * transitionRadians * (tapCount - 1));
            candidates.Add(Evaluate(
                $"fixed-{tapsPerPhase}-taps-per-phase",
                GenerateKaiser(tapCount, requirements, impliedAttenuationDb),
                impliedAttenuationDb,
                requirements));
        }

        foreach (var (name, aliasBudgetDb) in new[]
                 {
                     ("kaiser-no-alias-budget", 0d),
                     ("kaiser-power-sum-budget", 10 * Math.Log10(HopSize - 1d)),
                     ("kaiser-production-magnitude-sum-budget", 20 * Math.Log10(HopSize - 1d))
                 })
        {
            var requestedAttenuationDb = requirements.StopbandAttenuationDb + aliasBudgetDb;
            var designed = KaiserLowPassDesigner.Design(new LowPassFilterSpec(
                InputSampleRateHz,
                requirements.PassbandEdgeHz,
                requirements.StopbandEdgeHz,
                requirements.PassbandRippleDb,
                requestedAttenuationDb));
            candidates.Add(Evaluate(
                name,
                PadToPhaseBoundary(designed.Taps.Span),
                requestedAttenuationDb,
                requirements));
        }

        var document = new StudyDocument(
            SchemaVersion: 1,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            InputSampleRateHz,
            ChannelCount,
            ChannelSpacingHz,
            FftSize,
            HopSize,
            CoarseOutputSampleRateHz: requirements.CoarseOutputSampleRateHz,
            PassbandEdgeHz: requirements.PassbandEdgeHz,
            StopbandEdgeHz: requirements.StopbandEdgeHz,
            RequiredPassbandRippleDb: requirements.PassbandRippleDb,
            RequiredStopbandAttenuationDb: requirements.StopbandAttenuationDb,
            ProductionAliasBudgetDb: 20 * Math.Log10(HopSize - 1d),
            Candidates: candidates);

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(fullPath);
        foreach (var candidate in candidates)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"{candidate.Name}: {candidate.TapsPerPhase} taps/phase, ripple {candidate.PassbandRippleDb:F3} dB, stop {candidate.StopbandAttenuationDb:F1} dB, folded {candidate.WorstFoldedAliasAttenuationDb:F1} dB"));
        }
    }

    private static CandidateResult Evaluate(
        string name,
        float[] taps,
        double designAttenuationDb,
        PfbPrototypeRequirements requirements)
    {
        var specification = new LowPassFilterSpec(
            InputSampleRateHz,
            requirements.PassbandEdgeHz,
            requirements.StopbandEdgeHz,
            requirements.PassbandRippleDb,
            requirements.StopbandAttenuationDb);
        var response = FrequencyResponseEvaluator.MeasureLowPass(taps, specification, DenseResponsePoints);
        var dense = FrequencyResponseEvaluator.EvaluateDenseConservative(
            taps,
            InputSampleRateHz,
            DenseResponsePoints);
        var folded = AliasedResponseEvaluator.EvaluateConservative(
            dense,
            HopSize,
            requirements.PassbandEdgeHz,
            FoldedResponsePoints);
        return new CandidateResult(
            name,
            designAttenuationDb,
            taps.Length,
            taps.Length / FftSize,
            response.PassbandRippleDb,
            response.StopbandAttenuationDb,
            folded.WorstAliasAttenuationDb,
            response.PassbandRippleDb <= requirements.PassbandRippleDb + 1e-9 &&
            response.StopbandAttenuationDb + 1e-9 >= requirements.StopbandAttenuationDb,
            folded.WorstAliasAttenuationDb + 1e-9 >= requirements.StopbandAttenuationDb);
    }

    private static float[] GenerateKaiser(
        int tapCount,
        PfbPrototypeRequirements requirements,
        double designAttenuationDb)
    {
        var order = tapCount - 1;
        var cutoff = (requirements.PassbandEdgeHz + requirements.StopbandEdgeHz) /
                     (2 * InputSampleRateHz);
        var beta = KaiserBeta(designAttenuationDb);
        var denominator = ModifiedBesselI0(beta);
        var values = new double[tapCount];
        for (var index = 0; index < tapCount; index++)
        {
            var offset = index - (order / 2d);
            var ideal = Math.Abs(offset) < double.Epsilon
                ? 2 * cutoff
                : Math.Sin(2 * Math.PI * cutoff * offset) / (Math.PI * offset);
            var ratio = (2d * index / order) - 1;
            var window = ModifiedBesselI0(beta * Math.Sqrt(Math.Max(0, 1 - (ratio * ratio)))) / denominator;
            values[index] = ideal * window;
        }

        var sum = values.Sum();
        var taps = values.Select(value => (float)(value / sum)).ToArray();
        var floatSum = taps.Sum(value => (double)value);
        taps[taps.Length / 2] += (float)(1 - floatSum);
        return taps;
    }

    private static float[] PadToPhaseBoundary(ReadOnlySpan<float> source)
    {
        var paddedLength = checked(((source.Length + FftSize - 1) / FftSize) * FftSize);
        var taps = new float[paddedLength];
        source.CopyTo(taps.AsSpan((paddedLength - source.Length) / 2));
        return taps;
    }

    private static double KaiserBeta(double attenuationDb) => attenuationDb switch
    {
        > 50 => 0.1102 * (attenuationDb - 8.7),
        >= 21 => (0.5842 * Math.Pow(attenuationDb - 21, 0.4)) + (0.07886 * (attenuationDb - 21)),
        _ => 0
    };

    private static double ModifiedBesselI0(double value)
    {
        var halfSquared = value * value / 4;
        var sum = 1d;
        var term = 1d;
        for (var index = 1; index < 100; index++)
        {
            term *= halfSquared / (index * (double)index);
            sum += term;
            if (term <= sum * 1e-16)
            {
                break;
            }
        }

        return sum;
    }

    private static ChannelizerRequest Request()
    {
        var channels = Enumerable.Range(0, ChannelCount)
            .Select(index => new ChannelRequest(
                index + 1,
                (index - ((ChannelCount - 1) / 2d)) * ChannelSpacingHz,
                20_000,
                20_000,
                60,
                0.2))
            .ToArray();
        return new ChannelizerRequest(
            InputSampleRateHz,
            channels,
            ChannelizerStrategy.Pfb,
            new InputBlockConstraints(32_768, 32_768),
            new ChannelizerImplementationHints(PfbPrototypeDesign: PfbPrototypeDesignMode.Conservative));
    }

    private static string? ValueAfter(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index] == name)
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private sealed record StudyDocument(
        int SchemaVersion,
        DateTimeOffset GeneratedAtUtc,
        double InputSampleRateHz,
        int ChannelCount,
        double ChannelSpacingHz,
        int FftSize,
        int HopSize,
        double CoarseOutputSampleRateHz,
        double PassbandEdgeHz,
        double StopbandEdgeHz,
        double RequiredPassbandRippleDb,
        double RequiredStopbandAttenuationDb,
        double ProductionAliasBudgetDb,
        IReadOnlyList<CandidateResult> Candidates);

    private sealed record CandidateResult(
        string Name,
        double DesignAttenuationDb,
        int TapCount,
        int TapsPerPhase,
        double PassbandRippleDb,
        double StopbandAttenuationDb,
        double WorstFoldedAliasAttenuationDb,
        bool MeetsUnfoldedSpecification,
        bool MeetsConservativeFoldedSpecification);
}
