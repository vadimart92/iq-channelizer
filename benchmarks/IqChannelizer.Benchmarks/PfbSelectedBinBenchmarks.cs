using BenchmarkDotNet.Attributes;
using IqChannelizer.Abstractions;
using IqChannelizer.Fftw;
using IqChannelizer.Pfb;
using System.Runtime.Intrinsics.X86;

namespace IqChannelizer.Benchmarks;

[MemoryDiagnoser]
public class PfbSelectedBinBenchmarks
{
    private const int Frames = 128;
    private ComplexF[] _input = null!;
    private ComplexF[] _fullOutput = null!;
    private ComplexF[] _selectedOutput = null!;
    private int[] _bins = null!;
    private FftwComplexPlan _plan = null!;
    private PfbSelectedBinDft _candidate = null!;

    [Params(64, 512)]
    public int FftSize { get; set; }

    [Params(1, 4, 8)]
    public int UniqueBinCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _bins = Enumerable.Range(0, UniqueBinCount)
            .Select(index => index * FftSize / UniqueBinCount)
            .ToArray();
        _input = Enumerable.Range(0, FftSize * Frames)
            .Select(index => ComplexF.FromPolar(index * 0.013))
            .ToArray();
        _fullOutput = new ComplexF[_input.Length];
        _selectedOutput = new ComplexF[UniqueBinCount * Frames];
        _plan = new FftwComplexPlan(FftSize, Frames, FftwNative.Backward);
        _input.CopyTo(_plan.WritableInput);
        _candidate = new PfbSelectedBinDft(FftSize, _bins);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Frames)]
    public ComplexF FftwAndGather()
    {
        _plan.ExecuteFromInput(_fullOutput);
        for (var binIndex = 0; binIndex < _bins.Length; binIndex++)
        {
            var destination = _selectedOutput.AsSpan(binIndex * Frames, Frames);
            for (var frame = 0; frame < Frames; frame++)
            {
                destination[frame] = _fullOutput[(frame * FftSize) + _bins[binIndex]];
            }
        }

        return _selectedOutput[^1];
    }

    [Benchmark(OperationsPerInvoke = Frames)]
    public ComplexF DirectScalar()
    {
        _candidate.TransformScalar(_input, Frames, _selectedOutput);
        return _selectedOutput[^1];
    }

    [Benchmark(OperationsPerInvoke = Frames)]
    public ComplexF DirectAvx2()
    {
        _candidate.TransformAvx2(_input, Frames, _selectedOutput);
        return _selectedOutput[^1];
    }

    [Benchmark(OperationsPerInvoke = Frames)]
    public ComplexF DirectAvx512()
    {
        _candidate.TransformAvx512(_input, Frames, _selectedOutput);
        return _selectedOutput[^1];
    }

    [GlobalCleanup]
    public void Cleanup() => _plan.Dispose();
}

[MemoryDiagnoser]
public class PfbPrototypeBenchmarks
{
    private IStreamingChannelizer _engine = null!;
    private ComplexF[] _input = null!;
    private CountingSink _sink = null!;
    private long _firstNewSampleIndex;

    [Params(PfbPrototypeDesignMode.Conservative, PfbPrototypeDesignMode.FoldAware)]
    public PfbPrototypeDesignMode DesignMode { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var channels = Enumerable.Range(0, 8)
            .Select(index => new ChannelRequest(
                index,
                (index - 4) * 15_625,
                10_000,
                10_000,
                60,
                0.2))
            .ToArray();
        var simd = Avx512F.IsSupported
            ? SimdPreference.Avx512
            : Avx2.IsSupported && Fma.IsSupported ? SimdPreference.Avx2 : SimdPreference.Scalar;
        var request = new ChannelizerRequest(
            1_000_000,
            channels,
            ChannelizerStrategy.Pfb,
            new InputBlockConstraints(4096, 4096),
            new ChannelizerImplementationHints(
                PfbFftSize: 64,
                PfbHopSize: 32,
                PfbFramesPerBatch: 128,
                Simd: simd,
                PfbPrototypeDesign: DesignMode));
        _engine = ChannelizerFactory.Create(request);
        _input = Enumerable.Range(0, _engine.InputRequirements.InputSize)
            .Select(index => ComplexF.FromPolar(index * 0.013))
            .ToArray();
        _sink = new CountingSink();
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
