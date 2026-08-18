using BenchmarkDotNet.Attributes;
using IqChannelizer.Abstractions;
using IqChannelizer.Fftw;

namespace IqChannelizer.Benchmarks;

[MemoryDiagnoser]
public class FftwBenchmarks
{
    private FftwComplexPlan _plan = null!;
    private ComplexF[] _input = null!;
    private ComplexF[] _output = null!;

    [Params(1024, 4096)]
    public int TransformLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _plan = new FftwComplexPlan(TransformLength, 1, FftwNative.Forward);
        _input = new ComplexF[TransformLength];
        _output = new ComplexF[TransformLength];
        for (var index = 0; index < _input.Length; index++)
        {
            _input[index] = ComplexF.FromPolar(index * 0.013);
        }
    }

    [Benchmark]
    public ComplexF ForwardTransform()
    {
        _plan.Execute(_input, _output);
        return _output[0];
    }

    [GlobalCleanup]
    public void Cleanup() => _plan.Dispose();
}
