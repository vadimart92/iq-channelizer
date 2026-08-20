using System.Buffers.Binary;
using System.Formats.Tar;
using System.Text.Json;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Cli.Tests;

public sealed class CliTests
{
    private string temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"iqchannelizer-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(temporaryDirectory, recursive: true);

    [Test]
    public void ChannelSpecSupportsDefaultsAndAllOptionalFields()
    {
        var defaults = ChannelSpecParser.Parse("7:125000:20000:10000");
        var complete = ChannelSpecParser.Parse("8:-125000:21000:9000:70:0.2:48000:96000");

        Assert.Multiple(() =>
        {
            Assert.That(defaults, Is.EqualTo(new ChannelRequest(7, 125000, 20000, 10000)));
            Assert.That(complete.StopbandAttenuationDb, Is.EqualTo(70));
            Assert.That(complete.PassbandRippleDb, Is.EqualTo(0.2));
            Assert.That(complete.MinimumOutputSampleRateHz, Is.EqualTo(48000));
            Assert.That(complete.PreferredOutputSampleRateHz, Is.EqualTo(96000));
        });
    }

    [Test]
    public void ProcessingSpeedReportsAggregateAverageAndBlockExtremesRelativeToInputRate()
    {
        var speed = new ProcessingSpeedStatistics(inputSampleRateHz: 50, samplesPerBlock: 100);

        speed.Record(TimeSpan.FromSeconds(2));
        speed.Record(TimeSpan.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(speed.BlockCount, Is.EqualTo(2));
            Assert.That(speed.AverageRatio, Is.EqualTo(4d / 3).Within(1e-12));
            Assert.That(speed.MinimumRatio, Is.EqualTo(1));
            Assert.That(speed.MaximumRatio, Is.EqualTo(2));
            Assert.That(speed.AverageSamplesPerSecond, Is.EqualTo(200d / 3).Within(1e-12));
        });
    }

    [Test]
    public void SigMfPairReadsCf32LittleEndian()
    {
        var metadataPath = Path.Combine(temporaryDirectory, "capture.sigmf-meta");
        var dataPath = Path.Combine(temporaryDirectory, "capture.sigmf-data");
        File.WriteAllText(metadataPath,
            """{"global":{"core:datatype":"cf32_le","core:sample_rate":2048000},"captures":[],"annotations":[]}""");
        WriteComplexData(dataPath, [new ComplexF(0.25f, -0.5f), new ComplexF(-1, 0.75f)]);

        using var input = IqInput.Open(metadataPath);
        var samples = new ComplexF[4];
        var count = input.Read(samples);

        Assert.Multiple(() =>
        {
            Assert.That(input.SampleRateHz, Is.EqualTo(2048000));
            Assert.That(count, Is.EqualTo(2));
            Assert.That(samples[0], Is.EqualTo(new ComplexF(0.25f, -0.5f)));
            Assert.That(samples[1], Is.EqualTo(new ComplexF(-1, 0.75f)));
            Assert.That(input.Read(samples), Is.Zero);
        });
    }

    [Test]
    public void SigMfArchiveMayStoreDataBeforeMetadata()
    {
        var archivePath = Path.Combine(temporaryDirectory, "capture.sigmf");
        var buffer = Array.Empty<byte>();
        var data = SigMfChannelWriter.Encode([new ComplexF(0.5f, -0.25f)], ref buffer).ToArray();
        var metadata = """{"global":{"core:datatype":"cf32_le","core:sample_rate":48000},"captures":[],"annotations":[]}"""u8.ToArray();
        using (var stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new TarWriter(stream))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "capture.sigmf-data")
            {
                DataStream = new MemoryStream(data)
            });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "capture.sigmf-meta")
            {
                DataStream = new MemoryStream(metadata)
            });
        }

        using var input = IqInput.Open(archivePath);
        var sample = new ComplexF[1];
        Assert.Multiple(() =>
        {
            Assert.That(input.SampleRateHz, Is.EqualTo(48000));
            Assert.That(input.Read(sample), Is.EqualTo(1));
            Assert.That(sample[0], Is.EqualTo(new ComplexF(0.5f, -0.25f)));
        });
    }

    [Test]
    public void WavReadsStereoFloatIqAndSampleRate()
    {
        var path = Path.Combine(temporaryDirectory, "capture.wav");
        WriteFloatWav(path, 1024, [new ComplexF(0.25f, -0.5f), new ComplexF(-1, 0.75f)]);

        using var input = IqInput.Open(path);
        var samples = new ComplexF[4];
        var count = input.Read(samples);

        Assert.Multiple(() =>
        {
            Assert.That(input.SampleRateHz, Is.EqualTo(1024));
            Assert.That(count, Is.EqualTo(2));
            Assert.That(samples[0], Is.EqualTo(new ComplexF(0.25f, -0.5f)));
            Assert.That(samples[1], Is.EqualTo(new ComplexF(-1, 0.75f)));
        });
    }

    [Test]
    public async Task CommandProcessesFinalPartialBlockAndWritesSigMfMetadata()
    {
        var inputPath = Path.Combine(temporaryDirectory, "capture.wav");
        var outputPath = Path.Combine(temporaryDirectory, "out");
        WriteFloatWav(inputPath, 1024, Enumerable.Range(0, 34)
            .Select(index => new ComplexF(MathF.Cos(index), MathF.Sin(index))).ToArray());
        var options = new ChannelizeOptions(
            new FileInfo(inputPath),
            ["7:128:20:10"],
            new DirectoryInfo(outputPath),
            ChannelizerStrategy.Fdc,
            OutputFormat.SigMf,
            16,
            32,
            SimdPreference.Scalar);

        await ChannelizeCommand.RunAsync(options, CancellationToken.None);

        var dataPath = Path.Combine(outputPath, "channel-7.sigmf-data");
        var metadataPath = Path.Combine(outputPath, "channel-7.sigmf-meta");
        using var document = JsonDocument.Parse(File.ReadAllBytes(metadataPath));
        Assert.Multiple(() =>
        {
            Assert.That(new FileInfo(dataPath).Length, Is.EqualTo(16));
            Assert.That(document.RootElement.GetProperty("global").GetProperty("core:datatype").GetString(),
                Is.EqualTo("cf32_le"));
            Assert.That(document.RootElement.GetProperty("global").GetProperty("core:sample_rate").GetDouble(),
                Is.EqualTo(32));
        });
    }

    private static void WriteFloatWav(string path, uint sampleRate, IReadOnlyList<ComplexF> samples)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        Span<byte> header = stackalloc byte[44];
        var dataBytes = checked((uint)samples.Count * 8);
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 36 + dataBytes);
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], sampleRate * 8);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 8);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 32);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], dataBytes);
        stream.Write(header);
        var buffer = Array.Empty<byte>();
        stream.Write(SigMfChannelWriter.Encode(samples.ToArray(), ref buffer));
    }

    private static void WriteComplexData(string path, IReadOnlyList<ComplexF> samples)
    {
        var buffer = Array.Empty<byte>();
        File.WriteAllBytes(path, SigMfChannelWriter.Encode(samples.ToArray(), ref buffer).ToArray());
    }
}
