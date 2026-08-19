using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Benchmarks;

internal static class TargetRateProfileRunner
{
    private const double InputSampleRateHz = 100_000_000;
    private const int ChannelCount = 8;
    private const int ChunkSize = 32_768;
    private const int DefaultIterations = 500;
    private const int StabilizationIterations = 256;

    public static void Run(string[] args)
    {
        var outputPath = ValueAfter(args, "--output") ??
                         Path.Combine("artifacts", "benchmarks", "target-100ms-profile.json");
        var commit = ValueAfter(args, "--commit") ?? "unknown";
        var iterations = int.TryParse(ValueAfter(args, "--iterations"), out var requestedIterations)
            ? requestedIterations
            : DefaultIterations;
        if (iterations < 10)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Target-rate profile requires at least 10 iterations.");
        }

        var results = new[]
        {
            Profile(ChannelizerStrategy.Fdc, iterations),
            Profile(ChannelizerStrategy.Pfb, iterations)
        };
        var document = new TargetRateProfileDocument(
            SchemaVersion: 1,
            Commit: commit,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Environment: new EnvironmentProfile(
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount,
                Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown"),
            Configuration: new ProfileConfiguration(
                InputSampleRateHz,
                ChannelCount,
                ChunkSize,
                iterations,
                "15 kHz passband, 5 kHz transition, 60 dB stopband, centers aligned to K=4096 bins"),
            Results: results,
            AnyStrategyMeetsTarget: results.Any(result => result.MeetsTargetRate));

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(fullPath);
    }

    private static TargetRateResult Profile(ChannelizerStrategy strategy, int iterations)
    {
        using var engine = ChannelizerFactory.Create(Request(strategy));
        var input = new ComplexF[engine.InputRequirements.InputSize];
        var sink = new CountingSink();
        var latencies = new long[iterations];
        var firstNew = 0L;
        for (var iteration = 0; iteration < StabilizationIterations; iteration++)
        {
            engine.Process(input, firstNew, sink);
            firstNew += engine.InputRequirements.ChunkSize;
        }

        var baseline = engine.Diagnostics.Snapshot;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var startedAt = Stopwatch.GetTimestamp();
            engine.Process(input, firstNew, sink);
            latencies[iteration] = Stopwatch.GetTimestamp() - startedAt;
            firstNew += engine.InputRequirements.ChunkSize;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var diagnostics = engine.Diagnostics.Snapshot;
        Array.Sort(latencies);
        var processingTicks = diagnostics.ProcessingElapsedTicks - baseline.ProcessingElapsedTicks;
        var elapsedSeconds = processingTicks / (double)Stopwatch.Frequency;
        var inputSamples = checked((long)iterations * engine.InputRequirements.ChunkSize);
        var sustainedRate = inputSamples / elapsedSeconds / 1_000_000;
        return new TargetRateResult(
            Strategy: strategy.ToString(),
            SelectedSimdBackend: engine.Plan.SelectedSimdBackend.ToString(),
            FilterDesignMode: engine.Plan.FilterDesignMode,
            FftSize: engine.Plan.FftSize,
            HopSize: engine.Plan.HopSize,
            FramesPerBatch: engine.Plan.FramesPerBatch,
            HistorySize: engine.InputRequirements.HistorySize,
            ChunkSize: engine.InputRequirements.ChunkSize,
            EstimatedWorkingSetBytes: engine.Plan.EstimatedWorkingSetBytes,
            ManagedAllocatedBytes: allocated,
            NanosecondsPerInputSample: elapsedSeconds * 1_000_000_000 / inputSamples,
            SustainedInputMegaSamplesPerSecond: sustainedRate,
            RealtimeMarginAt100MegaSamplesPerSecond: sustainedRate / 100,
            MeetsTargetRate: sustainedRate >= 100,
            LatencyMilliseconds: new LatencyProfile(
                TicksToMilliseconds(Percentile(latencies, 0.50)),
                TicksToMilliseconds(Percentile(latencies, 0.95)),
                TicksToMilliseconds(Percentile(latencies, 0.99)),
                TicksToMilliseconds(latencies[^1])),
            OutputSamples: diagnostics.TotalOutputSamples - baseline.TotalOutputSamples,
            Checksum: sink.Checksum);
    }

    private static ChannelizerRequest Request(ChannelizerStrategy strategy)
    {
        const int fftSize = 4096;
        var binSpacing = InputSampleRateHz / fftSize;
        var channels = Enumerable.Range(0, ChannelCount)
            .Select(index => new ChannelRequest(
                index,
                (index - (ChannelCount / 2)) * binSpacing,
                15_000,
                5_000,
                60,
                0.2))
            .ToArray();
        var hints = strategy == ChannelizerStrategy.Fdc
            ? new ChannelizerImplementationHints(
                FdcDecimationFactor: 4096,
                Simd: SimdPreference.Auto,
                Diagnostics: DiagnosticsMode.StageTiming)
            : new ChannelizerImplementationHints(
                PfbFftSize: fftSize,
                PfbHopSize: 2048,
                PfbFramesPerBatch: 16,
                Simd: SimdPreference.Auto,
                Diagnostics: DiagnosticsMode.StageTiming,
                PfbPrototypeDesign: PfbPrototypeDesignMode.FoldAware);
        return new ChannelizerRequest(
            InputSampleRateHz,
            channels,
            strategy,
            new InputBlockConstraints(ChunkSize, ChunkSize),
            hints);
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

    private static long Percentile(IReadOnlyList<long> sorted, double percentile) =>
        sorted[(int)Math.Ceiling(percentile * sorted.Count) - 1];

    private static double TicksToMilliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    private sealed class CountingSink : IChannelOutputSink
    {
        public double Checksum { get; private set; }

        public void Write(int channelId, ReadOnlySpan<ComplexF> samples) =>
            Checksum += channelId + samples[0].Real + samples[^1].Imaginary;
    }

    private sealed record TargetRateProfileDocument(
        int SchemaVersion,
        string Commit,
        DateTimeOffset GeneratedAtUtc,
        EnvironmentProfile Environment,
        ProfileConfiguration Configuration,
        IReadOnlyList<TargetRateResult> Results,
        bool AnyStrategyMeetsTarget);

    private sealed record EnvironmentProfile(
        string Runtime,
        string OperatingSystem,
        string ProcessArchitecture,
        int LogicalProcessorCount,
        string ProcessorIdentifier);

    private sealed record ProfileConfiguration(
        double InputSampleRateHz,
        int ChannelCount,
        int ChunkSize,
        int Iterations,
        string SignalSpecification);

    private sealed record TargetRateResult(
        string Strategy,
        string SelectedSimdBackend,
        string? FilterDesignMode,
        int? FftSize,
        int? HopSize,
        int? FramesPerBatch,
        int HistorySize,
        int ChunkSize,
        long EstimatedWorkingSetBytes,
        long ManagedAllocatedBytes,
        double NanosecondsPerInputSample,
        double SustainedInputMegaSamplesPerSecond,
        double RealtimeMarginAt100MegaSamplesPerSecond,
        bool MeetsTargetRate,
        LatencyProfile LatencyMilliseconds,
        long OutputSamples,
        double Checksum);

    private sealed record LatencyProfile(double P50, double P95, double P99, double Maximum);
}
