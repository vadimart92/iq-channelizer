using System.Diagnostics;
using System.Globalization;
using IqChannelizer.Abstractions;
using IqChannelizer;

namespace IqChannelizer.Cli;

internal static class ChannelizeCommand
{
    public static Task RunAsync(ChannelizeOptions options, CancellationToken cancellationToken)
    {
        if (options.Channels.Count == 0)
        {
            throw new ArgumentException("At least one --channel value is required.");
        }

        if (options.PreferredChunkSize <= 0 || options.MaxChunkSize <= 0 ||
            options.PreferredChunkSize > options.MaxChunkSize)
        {
            throw new ArgumentException("Chunk sizes must be positive and --chunk-size must not exceed --max-chunk-size.");
        }

        using var input = IqInput.Open(options.Input.FullName);
        var channels = options.Channels.Select(ChannelSpecParser.Parse).ToArray();
        var request = new ChannelizerRequest(
            input.SampleRateHz,
            channels,
            options.Strategy,
            new InputBlockConstraints(options.PreferredChunkSize, options.MaxChunkSize),
            new ChannelizerImplementationHints(Simd: options.Simd));

        using var channelizer = ChannelizerFactory.Create(request);
        Directory.CreateDirectory(options.Output.FullName);
        using var sink = new ChannelFileSink(options.Output.FullName, channelizer.Plan.Channels, options.OutputFormat);

        var requirements = channelizer.InputRequirements;
        var buffer = new ComplexF[requirements.InputSize];
        var speed = new ProcessingSpeedStatistics(input.SampleRateHz, requirements.ChunkSize);
        long firstNewSampleIndex = 0;
        long inputSamples = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = buffer.AsSpan(requirements.HistorySize, requirements.ChunkSize);
            var samplesRead = input.Read(chunk);
            if (samplesRead == 0)
            {
                break;
            }

            chunk[samplesRead..].Clear();
            sink.BeginBlock(samplesRead);
            var processStarted = Stopwatch.GetTimestamp();
            channelizer.Process(buffer, firstNewSampleIndex, sink);
            speed.Record(Stopwatch.GetElapsedTime(processStarted));
            inputSamples = checked(inputSamples + samplesRead);
            if (samplesRead < requirements.ChunkSize)
            {
                break;
            }

            buffer.AsSpan(requirements.ChunkSize, requirements.HistorySize)
                .CopyTo(buffer.AsSpan(0, requirements.HistorySize));
            firstNewSampleIndex = checked(firstNewSampleIndex + requirements.ChunkSize);
        }

        sink.Complete();
        Console.WriteLine($"Processed {inputSamples:N0} IQ samples at {input.SampleRateHz:R} Hz using {channelizer.Plan.Strategy}.");
        Console.WriteLine(speed.BlockCount == 0
            ? "DSP speed / input SR: n/a (empty input)."
            : FormattableString.Invariant(
                $"DSP speed / input SR: avg {speed.AverageRatio:F2}x, min {speed.MinimumRatio:F2}x, max {speed.MaximumRatio:F2}x ({FormatSampleRate(speed.AverageSamplesPerSecond)} average, {speed.BlockCount:N0} blocks)."));
        foreach (var channel in channelizer.Plan.Channels)
        {
            Console.WriteLine(
                $"Channel {channel.ChannelId}: {channel.OutputSampleRateHz:R} Hz, " +
                $"{sink.GetSamplesWritten(channel.ChannelId):N0} samples -> {sink.GetDisplayPath(channel.ChannelId)}");
        }

        return Task.CompletedTask;
    }

    private static string FormatSampleRate(double samplesPerSecond)
    {
        var (value, suffix) = samplesPerSecond switch
        {
            >= 1_000_000_000 => (samplesPerSecond / 1_000_000_000, "GS/s"),
            >= 1_000_000 => (samplesPerSecond / 1_000_000, "MS/s"),
            >= 1_000 => (samplesPerSecond / 1_000, "kS/s"),
            _ => (samplesPerSecond, "S/s")
        };
        return value.ToString("F2", CultureInfo.InvariantCulture) + " " + suffix;
    }
}
