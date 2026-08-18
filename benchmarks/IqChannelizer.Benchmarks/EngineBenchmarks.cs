using BenchmarkDotNet.Attributes;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Benchmarks;

[MemoryDiagnoser]
public class EngineBenchmarks
{
    private const double SampleRate = 1_000_000;
    private IStreamingChannelizer _engine = null!;
    private ComplexF[] _input = null!;
    private CountingSink _sink = null!;
    private long _firstNewSampleIndex;

    [Params(ChannelizerStrategy.Fdc, ChannelizerStrategy.Pfb)]
    public ChannelizerStrategy Strategy { get; set; }

    [Params(1, 8, 32)]
    public int ChannelCount { get; set; }

    [GlobalSetup]
    public void Setup()
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
        var hints = Strategy == ChannelizerStrategy.Fdc
            ? new ChannelizerImplementationHints(FdcDecimationFactor: 32, Simd: SimdPreference.Scalar)
            : new ChannelizerImplementationHints(
                PfbFftSize: 64,
                PfbHopSize: 32,
                PfbFramesPerBatch: 128,
                Simd: SimdPreference.Scalar);
        var request = new ChannelizerRequest(
            SampleRate,
            channels,
            Strategy,
            new InputBlockConstraints(4096, 4096),
            hints);
        _engine = ChannelizerFactory.Create(request);
        _input = new ComplexF[_engine.InputRequirements.InputSize];
        _sink = new CountingSink();
        for (var index = 0; index < _input.Length; index++)
        {
            _input[index] = ComplexF.FromPolar(index * 0.013);
        }
    }

    [Benchmark(OperationsPerInvoke = 4096)]
    public int Process()
    {
        _sink.SampleCount = 0;
        _engine.Process(_input, _firstNewSampleIndex, _sink);
        _firstNewSampleIndex += _engine.InputRequirements.ChunkSize;
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
