using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Benchmarks;

internal static class OptimizationProfileRunner
{
    private const double InputSampleRateHz = 100_000_000;
    private const int ChannelCount = 100;
    private const int ChunkSize = 32_768;
    private const double ChannelSpacingHz = 500_000;
    private const int DefaultIterations = 1_000;
    private const int DefaultWarmupIterations = 256;

    public static void Run(string[] args)
    {
        var strategy = ParseStrategy(ValueAfter(args, "--strategy") ?? "pfb");
        var simd = ParseSimd(ValueAfter(args, "--simd") ?? "auto");
        var design = ParseDesign(ValueAfter(args, "--pfb-design") ?? "conservative");
        var iterations = PositiveInt(args, "--iterations", DefaultIterations);
        var warmupIterations = NonNegativeInt(args, "--warmup", DefaultWarmupIterations);
        var outputPath = ValueAfter(args, "--output") ??
                         Path.Combine("artifacts", "uprof", "optimization-profile.json");
        var commit = ValueAfter(args, "--commit") ?? "unknown";

        using var engine = ChannelizerFactory.Create(Request(strategy, simd, design));
        var input = new ComplexF[engine.InputRequirements.InputSize];
        for (var index = 0; index < input.Length; index++)
        {
            input[index] = ComplexF.FromPolar(index * 0.013);
        }

        var sink = new CountingSink();
        var firstNew = 0L;
        for (var iteration = 0; iteration < warmupIterations; iteration++)
        {
            engine.Process(input, firstNew, sink);
            firstNew += engine.InputRequirements.ChunkSize;
        }

        var baseline = engine.Diagnostics.Snapshot;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var startedAt = Stopwatch.GetTimestamp();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            engine.Process(input, firstNew, sink);
            firstNew += engine.InputRequirements.ChunkSize;
        }

        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var diagnostics = engine.Diagnostics.Snapshot;
        var totalSamples = checked((long)iterations * engine.InputRequirements.ChunkSize);
        var sustainedRate = totalSamples / elapsed.TotalSeconds / 1_000_000;
        var result = new ProfileDocument(
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
                20_000,
                20_000,
                60,
                0.2,
                ChannelSpacingHz,
                iterations,
                warmupIterations),
            Result: new ProfileResult(
                Strategy: strategy.ToString(),
                RequestedSimd: simd.ToString(),
                SelectedSimdBackend: engine.Plan.SelectedSimdBackend.ToString(),
                FilterDesignMode: engine.Plan.FilterDesignMode,
                FftSize: engine.Plan.FftSize,
                HopSize: engine.Plan.HopSize,
                FramesPerBatch: engine.Plan.FramesPerBatch,
                HistorySize: engine.InputRequirements.HistorySize,
                ChunkSize: engine.InputRequirements.ChunkSize,
                EstimatedWorkingSetBytes: engine.Plan.EstimatedWorkingSetBytes,
                ManagedAllocatedBytes: allocated,
                ElapsedSeconds: elapsed.TotalSeconds,
                NanosecondsPerInputSample: elapsed.TotalSeconds * 1_000_000_000 / totalSamples,
                SustainedInputMegaSamplesPerSecond: sustainedRate,
                RealtimeMarginAt100MegaSamplesPerSecond: sustainedRate / 100,
                StageTicks: new StageTicks(
                    strategy == ChannelizerStrategy.Fdc
                        ? diagnostics.FdcInputCopyElapsedTicks - baseline.FdcInputCopyElapsedTicks
                        : diagnostics.PfbPolyphaseElapsedTicks - baseline.PfbPolyphaseElapsedTicks,
                    diagnostics.FftwExecutionElapsedTicks - baseline.FftwExecutionElapsedTicks,
                    diagnostics.ChannelProcessingElapsedTicks - baseline.ChannelProcessingElapsedTicks,
                    diagnostics.OutputDeliveryElapsedTicks - baseline.OutputDeliveryElapsedTicks,
                    diagnostics.ProcessingElapsedTicks - baseline.ProcessingElapsedTicks),
                OutputSamples: diagnostics.TotalOutputSamples - baseline.TotalOutputSamples,
                Checksum: sink.Checksum));

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(fullPath);
        Console.WriteLine(FormattableString.Invariant(
            $"{strategy}/{engine.Plan.SelectedSimdBackend}: {sustainedRate:F3} MS/s, {sustainedRate / 100:F3}x realtime"));
    }

    private static ChannelizerRequest Request(
        ChannelizerStrategy strategy,
        SimdPreference simd,
        PfbPrototypeDesignMode design)
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
        var hints = new ChannelizerImplementationHints(
            Simd: simd,
            Diagnostics: DiagnosticsMode.StageTiming,
            PfbPrototypeDesign: design);
        return new ChannelizerRequest(
            InputSampleRateHz,
            channels,
            strategy,
            new InputBlockConstraints(ChunkSize, ChunkSize),
            hints);
    }

    private static ChannelizerStrategy ParseStrategy(string value) => value.ToLowerInvariant() switch
    {
        "fdc" => ChannelizerStrategy.Fdc,
        "pfb" => ChannelizerStrategy.Pfb,
        _ => throw new ArgumentException("--strategy must be fdc or pfb.")
    };

    private static SimdPreference ParseSimd(string value) => value.ToLowerInvariant() switch
    {
        "auto" => SimdPreference.Auto,
        "scalar" => SimdPreference.Scalar,
        "avx2" => SimdPreference.Avx2,
        "avx512" => SimdPreference.Avx512,
        _ => throw new ArgumentException("--simd must be auto, scalar, avx2, or avx512.")
    };

    private static PfbPrototypeDesignMode ParseDesign(string value) => value.ToLowerInvariant() switch
    {
        "conservative" => PfbPrototypeDesignMode.Conservative,
        "foldaware" => PfbPrototypeDesignMode.FoldAware,
        _ => throw new ArgumentException("--pfb-design must be conservative or foldaware.")
    };

    private static int PositiveInt(IReadOnlyList<string> args, string name, int defaultValue)
    {
        var value = ValueAfter(args, name);
        return value is null ? defaultValue : int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"{name} must be a positive integer.");
    }

    private static int NonNegativeInt(IReadOnlyList<string> args, string name, int defaultValue)
    {
        var value = ValueAfter(args, name);
        return value is null ? defaultValue : int.TryParse(value, out var parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException($"{name} must be a non-negative integer.");
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

    private sealed class CountingSink : IChannelOutputSink
    {
        public double Checksum { get; private set; }

        public void Write(int channelId, ReadOnlySpan<ComplexF> samples) =>
            Checksum += channelId + samples[0].Real + samples[^1].Imaginary;
    }

    private sealed record ProfileDocument(
        int SchemaVersion,
        string Commit,
        DateTimeOffset GeneratedAtUtc,
        EnvironmentProfile Environment,
        ProfileConfiguration Configuration,
        ProfileResult Result);

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
        double PassbandWidthHz,
        double TransitionWidthHz,
        double StopbandAttenuationDb,
        double PassbandRippleDb,
        double ChannelSpacingHz,
        int Iterations,
        int WarmupIterations);

    private sealed record ProfileResult(
        string Strategy,
        string RequestedSimd,
        string SelectedSimdBackend,
        string? FilterDesignMode,
        int? FftSize,
        int? HopSize,
        int? FramesPerBatch,
        int HistorySize,
        int ChunkSize,
        long EstimatedWorkingSetBytes,
        long ManagedAllocatedBytes,
        double ElapsedSeconds,
        double NanosecondsPerInputSample,
        double SustainedInputMegaSamplesPerSecond,
        double RealtimeMarginAt100MegaSamplesPerSecond,
        StageTicks StageTicks,
        long OutputSamples,
        double Checksum);

    private sealed record StageTicks(long Input, long Fftw, long Channel, long Output, long Total);
}
