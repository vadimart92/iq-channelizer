using BenchmarkDotNet.Attributes;
using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;

namespace IqChannelizer.Benchmarks;

[MemoryDiagnoser]
public class PrimitiveBenchmarks
{
    private ComplexF[] _input = null!;
    private float[] _taps = null!;
    private ComplexF[] _output = null!;

    [Params(31, 127)]
    public int TapCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        const int outputCount = 4096;
        _input = new ComplexF[outputCount + TapCount - 1];
        _taps = Enumerable.Repeat(1f / TapCount, TapCount).ToArray();
        _output = new ComplexF[outputCount];
        for (var index = 0; index < _input.Length; index++)
        {
            _input[index] = ComplexF.FromPolar(index * 0.013);
        }
    }

    [Benchmark(OperationsPerInvoke = 4096)]
    public ComplexF ScalarFir()
    {
        IqChannelizer.Dsp.ScalarFir.Filter(_input, _taps, _output);
        return _output[^1];
    }
}
