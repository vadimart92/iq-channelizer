using BenchmarkDotNet.Attributes;
using IqChannelizer.Abstractions;
using IqChannelizer.Pfb;

namespace IqChannelizer.Benchmarks;

[MemoryDiagnoser]
public class PfbFirBenchmarks
{
    private const int FftSize = 64;
    private const int HopSize = 32;
    private const int Frames = 128;
    private ComplexF[] _input = null!;
    private float[] _prototype = null!;
    private ComplexF[] _output = null!;
    private Avx2PfbCoefficients _coefficients = null!;
    private Avx512PfbCoefficients _avx512Coefficients = null!;

    [Params(8, 20)]
    public int TapsPerPhase { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _prototype = Enumerable.Repeat(1f / (FftSize * TapsPerPhase), FftSize * TapsPerPhase).ToArray();
        var history = _prototype.Length - 1;
        _input = new ComplexF[history + (HopSize * Frames)];
        _output = new ComplexF[FftSize * Frames];
        _coefficients = new Avx2PfbCoefficients(_prototype, FftSize);
        _avx512Coefficients = new Avx512PfbCoefficients(_prototype, FftSize);
        for (var index = 0; index < _input.Length; index++)
        {
            _input[index] = ComplexF.FromPolar(index * 0.013);
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = HopSize * Frames)]
    public ComplexF Scalar()
    {
        PfbPhaseFir.FillBatchScalar(
            _input,
            -(_prototype.Length - 1),
            0,
            HopSize,
            Frames,
            FftSize,
            _prototype,
            _output);
        return _output[^1];
    }

    [Benchmark(OperationsPerInvoke = HopSize * Frames)]
    public ComplexF Avx2Compact()
    {
        PfbPhaseFir.FillBatchAvx2Compact(
            _input,
            -(_prototype.Length - 1),
            0,
            HopSize,
            Frames,
            FftSize,
            _prototype,
            _output);
        return _output[^1];
    }

    [Benchmark(OperationsPerInvoke = HopSize * Frames)]
    public ComplexF Avx2Expanded()
    {
        PfbPhaseFir.FillBatchAvx2(
            _input,
            -(_prototype.Length - 1),
            0,
            HopSize,
            Frames,
            _prototype,
            _coefficients,
            _output);
        return _output[^1];
    }

    [Benchmark(OperationsPerInvoke = HopSize * Frames)]
    public ComplexF Avx512Expanded()
    {
        PfbPhaseFir.FillBatchAvx512(
            _input,
            -(_prototype.Length - 1),
            0,
            HopSize,
            Frames,
            _prototype,
            _avx512Coefficients,
            _output);
        return _output[^1];
    }
}
