using System.Runtime.Intrinsics.X86;
using IqChannelizer.Abstractions;
using IqChannelizer.Reference;

namespace IqChannelizer.Tests;

public sealed class SimdEngineTests
{
    [TestCase(ChannelizerStrategy.Fdc)]
    [TestCase(ChannelizerStrategy.Pfb)]
    public void ForcedAvx2MatchesScalarAcrossProcessPartitions(ChannelizerStrategy strategy)
    {
        if (!Avx2.IsSupported || !Fma.IsSupported)
        {
            Assert.Ignore("AVX2/FMA is not supported on this test host.");
        }

        var scalarRequest = CreateRequest(strategy, SimdPreference.Scalar);
        var avxRequest = CreateRequest(strategy, SimdPreference.Avx2);
        using var scalarEngine = ChannelizerFactory.Create(scalarRequest);
        using var avxEngine = ChannelizerFactory.Create(avxRequest);
        Assert.Multiple(() =>
        {
            Assert.That(avxEngine.Plan.SelectedSimdBackend, Is.EqualTo(SimdPreference.Avx2));
            Assert.That(avxEngine.Plan.DspBackend, Does.Contain("AVX2/FMA"));
            Assert.That(avxEngine.InputRequirements, Is.EqualTo(scalarEngine.InputRequirements));
        });

        const long initialFirstNew = (1L << 40) + 123;
        var firstNew = initialFirstNew;
        for (var block = 0; block < 5; block++)
        {
            var firstInput = firstNew - scalarEngine.InputRequirements.HistorySize;
            var input = DeterministicSignals.TwoTone(
                scalarEngine.InputRequirements.InputSize,
                127.75,
                0.8,
                -211.5,
                0.05,
                scalarRequest.InputSampleRateHz,
                firstInput);
            var scalarSink = new TestSink();
            var avxSink = new TestSink();
            scalarEngine.Process(input, firstNew, scalarSink);
            avxEngine.Process(input, firstNew, avxSink);

            Assert.That(avxSink.Blocks.Count, Is.EqualTo(scalarSink.Blocks.Count));
            for (var channelIndex = 0; channelIndex < scalarSink.Blocks.Count; channelIndex++)
            {
                var expected = scalarSink.Blocks[channelIndex];
                var actual = avxSink.Blocks[channelIndex];
                Assert.That(actual.ChannelId, Is.EqualTo(expected.ChannelId));
                Assert.That(actual.Samples.Length, Is.EqualTo(expected.Samples.Length));
                for (var index = 0; index < expected.Samples.Length; index++)
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(actual.Samples[index].Real,
                            Is.EqualTo(expected.Samples[index].Real).Within(2e-5f), $"block {block} real[{index}]");
                        Assert.That(actual.Samples[index].Imaginary,
                            Is.EqualTo(expected.Samples[index].Imaginary).Within(2e-5f), $"block {block} imag[{index}]");
                    });
                }
            }

            firstNew += scalarEngine.InputRequirements.ChunkSize;
        }
    }

    [Test]
    public void AutoSelectsBestAvailableImplementedBackendOnceAtCreation()
    {
        using var engine = ChannelizerFactory.Create(CreateRequest(ChannelizerStrategy.Pfb, SimdPreference.Auto));
        var expected = Avx2.IsSupported && Fma.IsSupported ? SimdPreference.Avx2 : SimdPreference.Scalar;
        Assert.That(engine.Plan.SelectedSimdBackend, Is.EqualTo(expected));
    }

    [TestCase(SimdPreference.Scalar)]
    [TestCase(SimdPreference.Avx2)]
    [NonParallelizable]
    public void RepresentativePfbProfileDoesNotAllocateAcrossTwoThousandCalls(SimdPreference simd)
    {
        if (simd == SimdPreference.Avx2 && (!Avx2.IsSupported || !Fma.IsSupported))
        {
            Assert.Ignore("AVX2/FMA is not supported on this test host.");
        }

        var channels = Enumerable.Range(0, 8)
            .Select(index => new ChannelRequest(
                index,
                (index - 4) * 15_625,
                10_000,
                10_000,
                60,
                0.2))
            .ToArray();
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
                Diagnostics: DiagnosticsMode.StageTiming));
        using var engine = ChannelizerFactory.Create(request);
        var input = new ComplexF[engine.InputRequirements.InputSize];
        var sink = new CountingSink();
        var firstNew = 0L;
        for (var iteration = 0; iteration < 2048; iteration++)
        {
            engine.Process(input, firstNew, sink);
            firstNew += engine.InputRequirements.ChunkSize;
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 2000; iteration++)
        {
            engine.Process(input, firstNew, sink);
            firstNew += engine.InputRequirements.ChunkSize;
        }

        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    private static ChannelizerRequest CreateRequest(ChannelizerStrategy strategy, SimdPreference simd) => new(
        1024,
        [
            new ChannelRequest(41, 128, 20, 10, 50, 0.2),
            new ChannelRequest(-7, -192, 18, 12, 50, 0.2)
        ],
        strategy,
        strategy == ChannelizerStrategy.Fdc
            ? new InputBlockConstraints(64, 64)
            : new InputBlockConstraints(12, 12),
        strategy == ChannelizerStrategy.Fdc
            ? new ChannelizerImplementationHints(FdcDecimationFactor: 2, Simd: simd)
            : new ChannelizerImplementationHints(
                PfbFftSize: 8,
                PfbHopSize: 3,
                PfbFramesPerBatch: 4,
                Simd: simd));

    private sealed class CountingSink : IChannelOutputSink
    {
        public double Checksum { get; private set; }

        public void Write(int channelId, ReadOnlySpan<ComplexF> samples) =>
            Checksum += channelId + samples[0].Real + samples[^1].Imaginary;
    }
}
