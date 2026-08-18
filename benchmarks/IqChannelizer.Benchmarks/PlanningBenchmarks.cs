using BenchmarkDotNet.Attributes;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Benchmarks;

[MemoryDiagnoser]
public class PlanningBenchmarks
{
    private ChannelizerRequest _request = null!;

    [Params(ChannelizerStrategy.Fdc, ChannelizerStrategy.Pfb)]
    public ChannelizerStrategy Strategy { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var hints = Strategy == ChannelizerStrategy.Fdc
            ? new ChannelizerImplementationHints(FdcDecimationFactor: 32, Simd: SimdPreference.Scalar)
            : new ChannelizerImplementationHints(
                PfbFftSize: 64,
                PfbHopSize: 32,
                PfbFramesPerBatch: 128,
                Simd: SimdPreference.Scalar);
        _request = new ChannelizerRequest(
            1_000_000,
            [new ChannelRequest(1, 125_000, 10_000, 10_000, 60, 0.2)],
            Strategy,
            new InputBlockConstraints(4096, 4096),
            hints);

        // Warm the native plan cache. This benchmark isolates managed layout,
        // filter validation, buffers, and plan-lease initialization from Process.
        using var warmup = ChannelizerFactory.Create(_request);
    }

    [Benchmark]
    public int CreateEngineWithWarmPlanCache()
    {
        using var engine = ChannelizerFactory.Create(_request);
        return engine.InputRequirements.InputSize;
    }
}
