using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using IqChannelizer.Abstractions;
using System.Runtime.Intrinsics.X86;

namespace IqChannelizer.Benchmarks;

internal static class StageProfileRunner
{
    private const double SampleRate = 1_000_000;
    private const int ChannelCount = 8;
    private const int ChunkSize = 4096;
    private const int DefaultIterations = 2000;
    private const int StabilizationIterations = 2048;

    public static void Run(string[] args)
    {
        var outputPath = ValueAfter(args, "--output") ??
                         Path.Combine("artifacts", "benchmarks", "stage-profile.json");
        var commit = ValueAfter(args, "--commit") ?? "unknown";
        var iterations = int.TryParse(ValueAfter(args, "--iterations"), out var requestedIterations)
            ? requestedIterations
            : DefaultIterations;
        if (iterations < 10)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Stage profile requires at least 10 iterations.");
        }

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var backends = Avx2.IsSupported && Fma.IsSupported
            ? new[] { SimdPreference.Scalar, SimdPreference.Avx2 }
            : new[] { SimdPreference.Scalar };
        var results = new List<EngineStageProfile>();
        foreach (var backend in backends)
        {
            results.Add(Profile(ChannelizerStrategy.Fdc, backend, iterations));
            results.Add(Profile(ChannelizerStrategy.Pfb, backend, iterations));
        }

        var document = new StageProfileDocument(
            SchemaVersion: 2,
            Commit: commit,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Environment: new EnvironmentProfile(
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount,
                Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown",
                Stopwatch.Frequency),
            Configuration: new ProfileConfiguration(SampleRate, ChannelCount, ChunkSize, iterations),
            Results: results);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(fullPath);
    }

    private static EngineStageProfile Profile(
        ChannelizerStrategy strategy,
        SimdPreference simdBackend,
        int iterations)
    {
        using (var warmup = ChannelizerFactory.Create(Request(strategy, simdBackend)))
        {
            var warmInput = new ComplexF[warmup.InputRequirements.InputSize];
            warmup.Process(warmInput, 0, new CountingSink());
        }

        using var engine = ChannelizerFactory.Create(Request(strategy, simdBackend));
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
        var workingSetBefore = Environment.WorkingSet;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var startedAt = Stopwatch.GetTimestamp();
            engine.Process(input, firstNew, sink);
            latencies[iteration] = Stopwatch.GetTimestamp() - startedAt;
            firstNew += engine.InputRequirements.ChunkSize;
        }

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var workingSetAfter = Environment.WorkingSet;
        var diagnostics = engine.Diagnostics.Snapshot;
        Array.Sort(latencies);
        var totalInputSamples = checked((long)iterations * engine.InputRequirements.ChunkSize);
        var processingTicks = diagnostics.ProcessingElapsedTicks - baseline.ProcessingElapsedTicks;
        var totalSeconds = processingTicks / (double)Stopwatch.Frequency;
        return new EngineStageProfile(
            Strategy: strategy.ToString(),
            SelectedSimdBackend: engine.Plan.SelectedSimdBackend.ToString(),
            DspBackend: engine.Plan.DspBackend,
            FftSize: engine.Plan.FftSize,
            HopSize: engine.Plan.HopSize,
            FramesPerBatch: engine.Plan.FramesPerBatch,
            HistorySize: engine.InputRequirements.HistorySize,
            ChunkSize: engine.InputRequirements.ChunkSize,
            EstimatedWorkingSetBytes: engine.Plan.EstimatedWorkingSetBytes,
            ProcessWorkingSetBytesBefore: workingSetBefore,
            ProcessWorkingSetBytesAfter: workingSetAfter,
            ManagedAllocatedBytes: allocatedBytes,
            NanosecondsPerInputSample: totalSeconds * 1_000_000_000 / totalInputSamples,
            SustainedInputMegaSamplesPerSecond: totalInputSamples / totalSeconds / 1_000_000,
            RealtimeMarginAtMetadataRate: diagnostics.CurrentRealtimeMargin,
            LatencyMilliseconds: new LatencyProfile(
                TicksToMilliseconds(Percentile(latencies, 0.50)),
                TicksToMilliseconds(Percentile(latencies, 0.95)),
                TicksToMilliseconds(Percentile(latencies, 0.99)),
                TicksToMilliseconds(latencies[^1])),
            StageTicks: new StageTicks(
                strategy == ChannelizerStrategy.Fdc
                    ? diagnostics.FdcInputCopyElapsedTicks - baseline.FdcInputCopyElapsedTicks
                    : diagnostics.PfbPolyphaseElapsedTicks - baseline.PfbPolyphaseElapsedTicks,
                diagnostics.FftwExecutionElapsedTicks - baseline.FftwExecutionElapsedTicks,
                diagnostics.ChannelProcessingElapsedTicks - baseline.ChannelProcessingElapsedTicks,
                diagnostics.OutputDeliveryElapsedTicks - baseline.OutputDeliveryElapsedTicks,
                processingTicks),
            OutputSamples: diagnostics.TotalOutputSamples - baseline.TotalOutputSamples,
            Checksum: sink.Checksum);
    }

    private static ChannelizerRequest Request(ChannelizerStrategy strategy, SimdPreference simdBackend)
    {
        var channels = Enumerable.Range(0, ChannelCount)
            .Select(index => new ChannelRequest(
                index,
                (index - (ChannelCount / 2)) * 15_625,
                10_000,
                10_000,
                60,
                0.2))
            .ToArray();
        var hints = strategy == ChannelizerStrategy.Fdc
            ? new ChannelizerImplementationHints(
                FdcDecimationFactor: 32,
                Simd: simdBackend,
                Diagnostics: DiagnosticsMode.StageTiming)
            : new ChannelizerImplementationHints(
                PfbFftSize: 64,
                PfbHopSize: 32,
                PfbFramesPerBatch: 128,
                Simd: simdBackend,
                Diagnostics: DiagnosticsMode.StageTiming);
        return new ChannelizerRequest(
            SampleRate,
            channels,
            strategy,
            new InputBlockConstraints(ChunkSize, ChunkSize),
            hints);
    }

    private static string? ValueAfter(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index] == name) return args[index + 1];
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

    private sealed record StageProfileDocument(
        int SchemaVersion,
        string Commit,
        DateTimeOffset GeneratedAtUtc,
        EnvironmentProfile Environment,
        ProfileConfiguration Configuration,
        IReadOnlyList<EngineStageProfile> Results);

    private sealed record EnvironmentProfile(
        string Runtime,
        string OperatingSystem,
        string ProcessArchitecture,
        int LogicalProcessorCount,
        string ProcessorIdentifier,
        long StopwatchFrequency);

    private sealed record ProfileConfiguration(
        double InputSampleRateHz,
        int ChannelCount,
        int ChunkSize,
        int Iterations);

    private sealed record EngineStageProfile(
        string Strategy,
        string SelectedSimdBackend,
        string DspBackend,
        int? FftSize,
        int? HopSize,
        int? FramesPerBatch,
        int HistorySize,
        int ChunkSize,
        long EstimatedWorkingSetBytes,
        long ProcessWorkingSetBytesBefore,
        long ProcessWorkingSetBytesAfter,
        long ManagedAllocatedBytes,
        double NanosecondsPerInputSample,
        double SustainedInputMegaSamplesPerSecond,
        double RealtimeMarginAtMetadataRate,
        LatencyProfile LatencyMilliseconds,
        StageTicks StageTicks,
        long OutputSamples,
        double Checksum);

    private sealed record LatencyProfile(double P50, double P95, double P99, double Maximum);

    private sealed record StageTicks(long Input, long Fftw, long Channel, long Output, long Total);
}
