using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
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

    [Benchmark]
    public ComplexF Avx512()
    {
        SpectralSliceExtractor.ExtractAvx512Unchecked(
            _spectrum,
            4000,
            _window,
            new ComplexF(0.8f, -0.6f),
            _output);
        return _output[^1];
    }
}

[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 3, exportCombinedDisassemblyReport: true, printSource: true)]
public class ResidualRotatorBenchmarks
{
    private ComplexF[] _samples = null!;
    private Rotator _scalarRotator = null!;
    private Rotator _avx2Rotator = null!;

    [GlobalSetup]
    public void Setup()
    {
        _samples = Enumerable.Range(0, 4096).Select(index => ComplexF.FromPolar(index * 0.013)).ToArray();
        _scalarRotator = new Rotator(12_345.25, 1_000_000, 7);
        _scalarRotator.SetPhaseFromAbsoluteIndex(1L << 48);
        _avx2Rotator = new Rotator(12_345.25, 1_000_000, 7, SimdPreference.Avx2);
        _avx2Rotator.SetPhaseFromAbsoluteIndex(1L << 48);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = 4096)]
    public ComplexF Scalar()
    {
        _scalarRotator.RotateInPlace(_samples);
        return _samples[^1];
    }

    [Benchmark(OperationsPerInvoke = 4096)]
    public ComplexF Avx2()
    {
        _avx2Rotator.RotateInPlace(_samples);
        return _samples[^1];
    }
}
