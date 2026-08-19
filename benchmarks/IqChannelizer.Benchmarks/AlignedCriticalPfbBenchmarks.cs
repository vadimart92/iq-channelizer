using BenchmarkDotNet.Attributes;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Benchmarks;

[MemoryDiagnoser]
public class AlignedCriticalPfbBenchmarks
{
    private const double SampleRate = 1_000_000;
    private const int HopSize = 32;
    private const int ChunkSize = 4096;
    private IStreamingChannelizer _engine = null!;
    private ComplexF[] _input = null!;
    private CountingSink _sink = null!;
    private long _firstNewSampleIndex;

    [Params(32, 64)]
    public int FftSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var binSpacing = SampleRate / 32;
        var channels = Enumerable.Range(0, 8)
            .Select(index => new ChannelRequest(
                index,
                (index - 4) * binSpacing,
                10_000,
                10_000,
                60,
                0.2))
            .ToArray();
        _engine = ChannelizerFactory.Create(new ChannelizerRequest(
            SampleRate,
            channels,
            ChannelizerStrategy.Pfb,
            new InputBlockConstraints(ChunkSize, ChunkSize),
            new ChannelizerImplementationHints(
                PfbFftSize: FftSize,
                PfbHopSize: HopSize,
                PfbFramesPerBatch: ChunkSize / HopSize,
                Simd: SimdPreference.Auto)));
        _input = new ComplexF[_engine.InputRequirements.InputSize];
        _sink = new CountingSink();
        for (var index = 0; index < _input.Length; index++)
        {
            _input[index] = ComplexF.FromPolar(index * 0.013);
        }
    }

    [Benchmark(OperationsPerInvoke = ChunkSize)]
    public int Process()
    {
        _sink.SampleCount = 0;
        _engine.Process(_input, _firstNewSampleIndex, _sink);
        _firstNewSampleIndex += ChunkSize;
        return _sink.SampleCount;
    }

    [GlobalCleanup]
    public void Cleanup() => _engine.Dispose();

    private sealed class CountingSink : IChannelOutputSink
    {
        public int SampleCount { get; set; }

        public void Write(int channelId, ReadOnlySpan<ComplexF> samples) =>
            SampleCount = checked(SampleCount + samples.Length);
    }
}
