using System.Buffers.Binary;
using System.Diagnostics;
using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Cli;

internal abstract class IqInput : IDisposable
{
    public abstract double SampleRateHz { get; }
    public abstract int Read(Span<ComplexF> destination);
    public abstract void Dispose();

    public static IqInput Open(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Input file was not found.", path);
        }

        if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            return new WavIqInput(path);
        }

        if (path.EndsWith(".sigmf", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".sigmf-meta", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".sigmf-data", StringComparison.OrdinalIgnoreCase))
        {
            return SigMfIqInput.Create(path);
        }

        throw new NotSupportedException("Input must have a .wav, .sigmf, .sigmf-meta, or .sigmf-data extension.");
    }
}

internal sealed class SigMfIqInput : IqInput
{
    private readonly Stream data;
    private readonly IDisposable[] owners;
    private readonly SigMfDataType dataType;
    private long remainingBytes;
    private byte[] byteBuffer = [];

    private SigMfIqInput(Stream data, long dataLength, SigMfMetadata metadata, params IDisposable[] owners)
    {
        if (dataLength % metadata.DataType.BytesPerSample != 0)
        {
            throw new InvalidDataException(
                $"SigMF data length is not a whole number of {metadata.DataType.Name} samples.");
        }

        this.data = data;
        this.owners = owners;
        dataType = metadata.DataType;
        remainingBytes = dataLength;
        SampleRateHz = metadata.SampleRateHz;
    }

    public override double SampleRateHz { get; }

    public static SigMfIqInput Create(string path)
    {
        if (path.EndsWith(".sigmf", StringComparison.OrdinalIgnoreCase))
        {
            return OpenArchive(path);
        }

        var metadataPath = path.EndsWith(".sigmf-meta", StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.ChangeExtension(path, ".sigmf-meta");
        var dataPath = path.EndsWith(".sigmf-data", StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.ChangeExtension(path, ".sigmf-data");
        if (!File.Exists(metadataPath) || !File.Exists(dataPath))
        {
            throw new FileNotFoundException(
                $"SigMF requires both '{Path.GetFileName(metadataPath)}' and '{Path.GetFileName(dataPath)}'.");
        }

        var metadata = ParseMetadata(File.ReadAllBytes(metadataPath));
        var stream = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new SigMfIqInput(stream, stream.Length, metadata, stream);
    }

    public override int Read(Span<ComplexF> destination)
    {
        if (destination.IsEmpty || remainingBytes == 0)
        {
            return 0;
        }

        var requestedBytes = checked(destination.Length * dataType.BytesPerSample);
        var bytesToRead = (int)Math.Min(requestedBytes, remainingBytes);
        if (byteBuffer.Length < bytesToRead)
        {
            byteBuffer = new byte[bytesToRead];
        }

        var totalRead = 0;
        while (totalRead < bytesToRead)
        {
            var count = data.Read(byteBuffer, totalRead, bytesToRead - totalRead);
            if (count == 0)
            {
                throw new EndOfStreamException("SigMF data ended before its declared length.");
            }

            totalRead += count;
        }

        remainingBytes -= totalRead;
        var sampleCount = totalRead / dataType.BytesPerSample;
        dataType.Decode(byteBuffer.AsSpan(0, totalRead), destination[..sampleCount]);
        return sampleCount;
    }

    public override void Dispose()
    {
        foreach (var owner in owners)
        {
            owner.Dispose();
        }
    }

    private static SigMfIqInput OpenArchive(string path)
    {
        byte[]? metadataBytes = null;
        using (var scanStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var scanReader = new TarReader(scanStream))
        {
            TarEntry? entry;
            while ((entry = scanReader.GetNextEntry()) is not null)
            {
                if (entry.Name.EndsWith(".sigmf-meta", StringComparison.OrdinalIgnoreCase) && entry.DataStream is not null)
                {
                    using var memory = new MemoryStream();
                    entry.DataStream.CopyTo(memory);
                    metadataBytes = memory.ToArray();
                    break;
                }
            }
        }

        if (metadataBytes is null)
        {
            throw new InvalidDataException("The SigMF archive does not contain a .sigmf-meta entry.");
        }

        var archiveStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var archiveReader = new TarReader(archiveStream, leaveOpen: true);
        TarEntry? dataEntry;
        while ((dataEntry = archiveReader.GetNextEntry()) is not null &&
               !dataEntry.Name.EndsWith(".sigmf-data", StringComparison.OrdinalIgnoreCase))
        {
        }

        if (dataEntry?.DataStream is null)
        {
            archiveReader.Dispose();
            archiveStream.Dispose();
            throw new InvalidDataException("The SigMF archive does not contain a .sigmf-data entry.");
        }

        var metadata = ParseMetadata(metadataBytes);
        return new SigMfIqInput(dataEntry.DataStream, dataEntry.Length, metadata, archiveReader, archiveStream);
    }

    private static SigMfMetadata ParseMetadata(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        if (!document.RootElement.TryGetProperty("global", out var global) ||
            !global.TryGetProperty("core:sample_rate", out var sampleRateElement) ||
            !sampleRateElement.TryGetDouble(out var sampleRate) ||
            !double.IsFinite(sampleRate) || sampleRate <= 0)
        {
            throw new InvalidDataException("SigMF metadata must contain a positive global core:sample_rate.");
        }

        if (!global.TryGetProperty("core:datatype", out var dataTypeElement) ||
            dataTypeElement.GetString() is not { } dataTypeName)
        {
            throw new InvalidDataException("SigMF metadata must contain global core:datatype.");
        }

        return new SigMfMetadata(sampleRate, SigMfDataType.Parse(dataTypeName));
    }

    private sealed record SigMfMetadata(double SampleRateHz, SigMfDataType DataType);
}

internal sealed class SigMfDataType
{
    private readonly char kind;
    private readonly int bits;
    private readonly bool littleEndian;

    private SigMfDataType(string name, char kind, int bits, bool littleEndian)
    {
        Name = name;
        this.kind = kind;
        this.bits = bits;
        this.littleEndian = littleEndian;
        BytesPerSample = checked(2 * bits / 8);
    }

    public string Name { get; }
    public int BytesPerSample { get; }

    public static SigMfDataType Parse(string value)
    {
        var parts = value.ToLowerInvariant().Split('_');
        var type = parts[0];
        if (type.Length < 3 || type[0] != 'c' || type[1] is not ('f' or 'i' or 'u') ||
            !int.TryParse(type.AsSpan(2), out var bits))
        {
            throw Unsupported(value);
        }

        var kind = type[1];
        var validBits = kind switch
        {
            'f' => bits is 32 or 64,
            _ => bits is 8 or 16 or 32
        };
        if (!validBits || (bits > 8 && (parts.Length != 2 || parts[1] is not ("le" or "be"))) || parts.Length > 2)
        {
            throw Unsupported(value);
        }

        return new SigMfDataType(value, kind, bits, parts.Length == 1 || parts[1] == "le");
    }

    public void Decode(ReadOnlySpan<byte> source, Span<ComplexF> destination)
    {
        var componentBytes = bits / 8;
        for (var index = 0; index < destination.Length; index++)
        {
            var sample = source.Slice(index * componentBytes * 2, componentBytes * 2);
            destination[index] = new ComplexF(
                DecodeComponent(sample[..componentBytes]),
                DecodeComponent(sample[componentBytes..]));
        }
    }

    private float DecodeComponent(ReadOnlySpan<byte> value)
    {
        if (kind == 'f')
        {
            if (bits == 32)
            {
                var raw = littleEndian
                    ? BinaryPrimitives.ReadInt32LittleEndian(value)
                    : BinaryPrimitives.ReadInt32BigEndian(value);
                return BitConverter.Int32BitsToSingle(raw);
            }

            var raw64 = littleEndian
                ? BinaryPrimitives.ReadInt64LittleEndian(value)
                : BinaryPrimitives.ReadInt64BigEndian(value);
            return (float)BitConverter.Int64BitsToDouble(raw64);
        }

        if (kind == 'i')
        {
            var signed = bits switch
            {
                8 => (sbyte)value[0],
                16 when littleEndian => BinaryPrimitives.ReadInt16LittleEndian(value),
                16 => BinaryPrimitives.ReadInt16BigEndian(value),
                32 when littleEndian => BinaryPrimitives.ReadInt32LittleEndian(value),
                _ => BinaryPrimitives.ReadInt32BigEndian(value)
            };
            return (float)(signed / Math.Pow(2, bits - 1));
        }

        var unsigned = bits switch
        {
            8 => value[0],
            16 when littleEndian => BinaryPrimitives.ReadUInt16LittleEndian(value),
            16 => BinaryPrimitives.ReadUInt16BigEndian(value),
            32 when littleEndian => BinaryPrimitives.ReadUInt32LittleEndian(value),
            _ => BinaryPrimitives.ReadUInt32BigEndian(value)
        };
        var midpoint = Math.Pow(2, bits - 1);
        return (float)((unsigned - midpoint) / midpoint);
    }

    private static NotSupportedException Unsupported(string value) => new(
        $"Unsupported SigMF datatype '{value}'. Supported complex datatypes are cf32/cf64, ci8/ci16/ci32, and cu8/cu16/cu32 with _le or _be where applicable.");
}

internal sealed class WavIqInput : IqInput
{
    private readonly FileStream stream;
    private readonly int format;
    private readonly int bitsPerSample;
    private readonly int blockAlign;
    private long remainingBytes;
    private byte[] byteBuffer = [];

    public WavIqInput(string path)
    {
        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var info = ReadHeader(stream);
            format = info.Format;
            bitsPerSample = info.BitsPerSample;
            blockAlign = info.BlockAlign;
            SampleRateHz = info.SampleRate;
            remainingBytes = info.DataLength;
            stream.Position = info.DataOffset;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public override double SampleRateHz { get; }

    public override int Read(Span<ComplexF> destination)
    {
        var samplesToRead = (int)Math.Min(destination.Length, remainingBytes / blockAlign);
        if (samplesToRead == 0)
        {
            if (remainingBytes != 0)
            {
                throw new InvalidDataException("WAV data chunk ends with a partial sample frame.");
            }

            return 0;
        }

        var bytesToRead = checked(samplesToRead * blockAlign);
        if (byteBuffer.Length < bytesToRead)
        {
            byteBuffer = new byte[bytesToRead];
        }

        stream.ReadExactly(byteBuffer.AsSpan(0, bytesToRead));
        remainingBytes -= bytesToRead;
        var componentBytes = bitsPerSample / 8;
        for (var index = 0; index < samplesToRead; index++)
        {
            var frame = byteBuffer.AsSpan(index * blockAlign, blockAlign);
            destination[index] = new ComplexF(
                DecodeComponent(frame[..componentBytes]),
                DecodeComponent(frame.Slice(componentBytes, componentBytes)));
        }

        return samplesToRead;
    }

    public override void Dispose() => stream.Dispose();

    private float DecodeComponent(ReadOnlySpan<byte> value)
    {
        if (format == 3)
        {
            return bitsPerSample == 32
                ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(value))
                : (float)BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(value));
        }

        return bitsPerSample switch
        {
            8 => (value[0] - 128) / 128f,
            16 => BinaryPrimitives.ReadInt16LittleEndian(value) / 32768f,
            24 => ReadInt24LittleEndian(value) / 8388608f,
            32 => (float)(BinaryPrimitives.ReadInt32LittleEndian(value) / 2147483648d),
            _ => throw new UnreachableException()
        };
    }

    private static WavInfo ReadHeader(FileStream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (ReadFourCc(reader) != "RIFF" || reader.ReadUInt32() < 4 || ReadFourCc(reader) != "WAVE")
        {
            throw new InvalidDataException("Input is not a little-endian RIFF/WAVE file.");
        }

        byte[]? formatBytes = null;
        long dataOffset = -1;
        long dataLength = -1;
        while (stream.Position <= stream.Length - 8)
        {
            var chunkId = ReadFourCc(reader);
            var chunkLength = reader.ReadUInt32();
            var chunkStart = stream.Position;
            var chunkEnd = checked(chunkStart + chunkLength);
            if (chunkEnd > stream.Length)
            {
                throw new InvalidDataException($"WAV chunk '{chunkId}' extends beyond the file.");
            }

            if (chunkId == "fmt ")
            {
                if (chunkLength > int.MaxValue)
                {
                    throw new InvalidDataException("WAV format chunk is too large.");
                }

                formatBytes = reader.ReadBytes((int)chunkLength);
            }
            else if (chunkId == "data" && dataOffset < 0)
            {
                dataOffset = chunkStart;
                dataLength = chunkLength;
            }

            stream.Position = checked(chunkEnd + (chunkLength & 1));
        }

        if (formatBytes is null || formatBytes.Length < 16 || dataOffset < 0)
        {
            throw new InvalidDataException("WAV must contain valid fmt and data chunks.");
        }

        var format = BinaryPrimitives.ReadUInt16LittleEndian(formatBytes);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(formatBytes.AsSpan(2));
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(formatBytes.AsSpan(4));
        var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(formatBytes.AsSpan(12));
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(formatBytes.AsSpan(14));
        if (format == 0xfffe)
        {
            if (formatBytes.Length < 40)
            {
                throw new InvalidDataException("WAVE_FORMAT_EXTENSIBLE fmt chunk is incomplete.");
            }

            format = BinaryPrimitives.ReadUInt16LittleEndian(formatBytes.AsSpan(24));
        }

        var valid = format switch
        {
            1 => bits is 8 or 16 or 24 or 32,
            3 => bits is 32 or 64,
            _ => false
        };
        if (!valid)
        {
            throw new NotSupportedException($"Unsupported WAV encoding (format {format}, {bits} bits).");
        }

        var expectedBlockAlign = checked(2 * bits / 8);
        if (channels != 2 || blockAlign != expectedBlockAlign || sampleRate == 0)
        {
            throw new InvalidDataException("IQ WAV input must contain exactly two tightly packed I/Q channels and a positive sample rate.");
        }

        return new WavInfo(format, bits, blockAlign, sampleRate, dataOffset, dataLength);
    }

    private static string ReadFourCc(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (bytes.Length != 4)
        {
            throw new EndOfStreamException("Unexpected end of WAV header.");
        }

        return Encoding.ASCII.GetString(bytes);
    }

    private static int ReadInt24LittleEndian(ReadOnlySpan<byte> value)
    {
        var result = value[0] | (value[1] << 8) | (value[2] << 16);
        return (result & 0x800000) == 0 ? result : result | unchecked((int)0xff000000);
    }

    private sealed record WavInfo(
        int Format,
        int BitsPerSample,
        int BlockAlign,
        uint SampleRate,
        long DataOffset,
        long DataLength);
}
