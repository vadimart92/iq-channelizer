using BenchmarkDotNet.Attributes;
using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;

namespace IqChannelizer.Benchmarks;

[MemoryDiagnoser]
public class FdcExtractionBenchmarks
{
    private ComplexF[] _spectrum = null!;
    private ComplexF[] _window = null!;
    private ComplexF[] _output = null!;

    [Params(128, 512)]
    public int SliceLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _spectrum = Enumerable.Range(0, 4096).Select(index => ComplexF.FromPolar(index * 0.013)).ToArray();
        _window = Enumerable.Range(0, SliceLength).Select(index => ComplexF.FromPolar(index * 0.007) * 0.5f).ToArray();
        _output = new ComplexF[SliceLength];
    }

    [Benchmark(Baseline = true)]
    public ComplexF Scalar()
    {
        SpectralSliceExtractor.ExtractUnchecked(_spectrum, 4000, _window, new ComplexF(0.8f, -0.6f), _output);
        return _output[^1];
    }

    [Benchmark]
    public ComplexF Avx2()
    {
        SpectralSliceExtractor.ExtractAvx2Unchecked(_spectrum, 4000, _window, new ComplexF(0.8f, -0.6f), _output);
        return _output[^1];
    }
}

[MemoryDiagnoser]
public class ResidualRotatorBenchmarks
{
    private ComplexF[] _samples = null!;

    [GlobalSetup]
    public void Setup() =>
        _samples = Enumerable.Range(0, 4096).Select(index => ComplexF.FromPolar(index * 0.013)).ToArray();

    [Benchmark(Baseline = true, OperationsPerInvoke = 4096)]
    public ComplexF Scalar()
    {
        ScalarRotator.RotateInPlace(_samples, 12_345.25, 1_000_000, 1L << 48, 7);
        return _samples[^1];
    }

    [Benchmark(OperationsPerInvoke = 4096)]
    public ComplexF Avx2()
    {
        ScalarRotator.RotateInPlaceAvx2(_samples, 12_345.25, 1_000_000, 1L << 48, 7);
        return _samples[^1];
    }
}
