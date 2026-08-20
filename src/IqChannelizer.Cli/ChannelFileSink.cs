using System.Buffers.Binary;
using System.Text.Json;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Cli;

internal sealed class ChannelFileSink : IChannelOutputSink, IDisposable
{
    private readonly Dictionary<int, ChannelTarget> targets;
    private int validInputSamples = -1;
    private bool completed;

    public ChannelFileSink(
        string outputDirectory,
        IReadOnlyList<ResolvedChannelPlan> channels,
        OutputFormat format)
    {
        var displayPaths = channels.ToDictionary(
            channel => channel.ChannelId,
            channel => Path.Combine(
                outputDirectory,
                $"channel-{channel.ChannelId}" + (format == OutputFormat.Wav ? ".wav" : ".sigmf-data")));
        var allFinalPaths = displayPaths.Values.SelectMany(path => format == OutputFormat.Wav
            ? new[] { path }
            : new[] { path, Path.ChangeExtension(path, ".sigmf-meta") });
        var existing = allFinalPaths.FirstOrDefault(File.Exists);
        if (existing is not null)
        {
            throw new IOException($"Output file already exists: {existing}");
        }

        targets = [];
        try
        {
            foreach (var channel in channels)
            {
                targets.Add(
                    channel.ChannelId,
                    new ChannelTarget(
                        CreateWriter(displayPaths[channel.ChannelId], channel.OutputSampleRateHz, format),
                        channel.InputSamplesPerOutputSample));
            }
        }
        catch
        {
            foreach (var target in targets.Values)
            {
                target.Writer.Dispose();
            }

            throw;
        }
    }

    public void BeginBlock(int inputSamples)
    {
        if (inputSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputSamples));
        }

        validInputSamples = inputSamples;
    }

    public void Write(int channelId, ReadOnlySpan<ComplexF> samples)
    {
        if (validInputSamples < 0)
        {
            throw new InvalidOperationException("BeginBlock must be called before writing channel output.");
        }

        var target = targets[channelId];
        var numerator = target.InputSamplesPerOutput.Numerator;
        var denominator = target.InputSamplesPerOutput.Denominator;
        var validOutput = checked((int)Math.Min(
            samples.Length,
            ((long)validInputSamples * denominator + numerator - 1) / numerator));
        target.Writer.Write(samples[..validOutput]);
    }

    public long GetSamplesWritten(int channelId) => targets[channelId].Writer.SamplesWritten;
    public string GetDisplayPath(int channelId) => targets[channelId].Writer.DisplayPath;

    public void Complete()
    {
        foreach (var target in targets.Values)
        {
            target.Writer.Complete();
        }

        completed = true;
    }

    public void Dispose()
    {
        foreach (var target in targets.Values)
        {
            target.Writer.Dispose();
        }

        if (!completed)
        {
            Console.Error.WriteLine("Incomplete channel output was discarded.");
        }
    }

    private static IChannelWriter CreateWriter(string path, double sampleRate, OutputFormat format) => format switch
    {
        OutputFormat.SigMf => new SigMfChannelWriter(path, sampleRate),
        OutputFormat.Wav => new WavChannelWriter(path, sampleRate),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private sealed record ChannelTarget(IChannelWriter Writer, RationalSampleOffset InputSamplesPerOutput);
}

internal interface IChannelWriter : IDisposable
{
    string DisplayPath { get; }
    long SamplesWritten { get; }
    void Write(ReadOnlySpan<ComplexF> samples);
    void Complete();
}

internal sealed class SigMfChannelWriter : IChannelWriter
{
    private readonly string dataPath;
    private readonly string metadataPath;
    private readonly string temporaryDataPath;
    private readonly string temporaryMetadataPath;
    private readonly double sampleRate;
    private FileStream? stream;
    private byte[] byteBuffer = [];
    private bool completed;

    public SigMfChannelWriter(string dataPath, double sampleRate)
    {
        this.dataPath = dataPath;
        metadataPath = Path.ChangeExtension(dataPath, ".sigmf-meta");
        temporaryDataPath = TemporaryPath(dataPath);
        temporaryMetadataPath = TemporaryPath(metadataPath);
        this.sampleRate = sampleRate;
        stream = new FileStream(temporaryDataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }

    public string DisplayPath => dataPath;
    public long SamplesWritten { get; private set; }

    public void Write(ReadOnlySpan<ComplexF> samples)
    {
        var bytes = Encode(samples, ref byteBuffer);
        stream!.Write(bytes);
        SamplesWritten = checked(SamplesWritten + samples.Length);
    }

    public void Complete()
    {
        stream!.Flush(flushToDisk: true);
        stream.Dispose();
        stream = null;

        using (var metadata = new FileStream(
                   temporaryMetadataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var document = new Dictionary<string, object>
            {
                ["global"] = new Dictionary<string, object>
                {
                    ["core:datatype"] = "cf32_le",
                    ["core:sample_rate"] = sampleRate,
                    ["core:version"] = "1.2.6",
                    ["core:recorder"] = "IqChannelizer CLI",
                    ["core:description"] = "Complex baseband channel output."
                },
                ["captures"] = new[]
                {
                    new Dictionary<string, long> { ["core:sample_start"] = 0 }
                },
                ["annotations"] = Array.Empty<object>()
            };
            JsonSerializer.Serialize(metadata, document, new JsonSerializerOptions { WriteIndented = true });
            metadata.WriteByte((byte)'\n');
        }

        var dataMoved = false;
        try
        {
            File.Move(temporaryDataPath, dataPath);
            dataMoved = true;
            File.Move(temporaryMetadataPath, metadataPath);
            completed = true;
        }
        catch
        {
            if (dataMoved)
            {
                File.Delete(dataPath);
            }

            throw;
        }
    }

    public void Dispose()
    {
        stream?.Dispose();
        if (!completed)
        {
            File.Delete(temporaryDataPath);
            File.Delete(temporaryMetadataPath);
        }
    }

    internal static ReadOnlySpan<byte> Encode(ReadOnlySpan<ComplexF> samples, ref byte[] buffer)
    {
        var length = checked(samples.Length * 8);
        if (buffer.Length < length)
        {
            buffer = new byte[length];
        }

        var bytes = buffer.AsSpan(0, length);
        for (var index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.Slice(index * 8, 4), BitConverter.SingleToInt32Bits(samples[index].Real));
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.Slice(index * 8 + 4, 4), BitConverter.SingleToInt32Bits(samples[index].Imaginary));
        }

        return bytes;
    }

    private static string TemporaryPath(string finalPath) =>
        $"{finalPath}.{Guid.NewGuid():N}.partial";
}

internal sealed class WavChannelWriter : IChannelWriter
{
    private const long MaximumDataBytes = uint.MaxValue - 36L;
    private readonly string path;
    private readonly string temporaryPath;
    private readonly uint sampleRate;
    private FileStream? stream;
    private byte[] byteBuffer = [];
    private bool completed;
    private long dataBytes;

    public WavChannelWriter(string path, double sampleRate)
    {
        if (sampleRate != Math.Truncate(sampleRate) || sampleRate is < 1 or > uint.MaxValue / 8d)
        {
            throw new NotSupportedException(
                $"WAV output requires an integer sample rate no greater than {uint.MaxValue / 8u}, got {sampleRate:R}. Use --output-format sigmf.");
        }

        this.path = path;
        temporaryPath = $"{path}.{Guid.NewGuid():N}.partial";
        this.sampleRate = (uint)sampleRate;
        stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        WriteHeader(stream, this.sampleRate, 0);
    }

    public string DisplayPath => path;
    public long SamplesWritten { get; private set; }

    public void Write(ReadOnlySpan<ComplexF> samples)
    {
        var nextDataBytes = checked(dataBytes + (long)samples.Length * 8);
        if (nextDataBytes > MaximumDataBytes)
        {
            throw new IOException("WAV output exceeds the classic RIFF 4 GiB limit. Use --output-format sigmf.");
        }

        var bytes = SigMfChannelWriter.Encode(samples, ref byteBuffer);
        stream!.Write(bytes);
        dataBytes = nextDataBytes;
        SamplesWritten = checked(SamplesWritten + samples.Length);
    }

    public void Complete()
    {
        stream!.Position = 0;
        WriteHeader(stream, sampleRate, checked((uint)dataBytes));
        stream.Flush(flushToDisk: true);
        stream.Dispose();
        stream = null;
        File.Move(temporaryPath, path);
        completed = true;
    }

    public void Dispose()
    {
        stream?.Dispose();
        if (!completed)
        {
            File.Delete(temporaryPath);
        }
    }

    private static void WriteHeader(Stream destination, uint sampleRate, uint dataLength)
    {
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked(36u + dataLength));
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], checked(sampleRate * 8));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 8);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 32);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], dataLength);
        destination.Write(header);
    }
}
