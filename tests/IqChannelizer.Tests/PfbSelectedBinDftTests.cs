using System.Runtime.Intrinsics.X86;
using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;
using IqChannelizer.Pfb;

namespace IqChannelizer.Tests;

public sealed class PfbSelectedBinDftTests
{
    [TestCase(8, 3)]
    [TestCase(16, 5)]
    [TestCase(64, 4)]
    public void SelectedBinKernelsMatchBackwardDft(int fftSize, int frames)
    {
        var bins = new[] { 0, 1, fftSize / 2, fftSize - 1 };
        var random = new Random(fftSize * 100 + frames);
        var input = Enumerable.Range(0, fftSize * frames)
            .Select(_ => new ComplexF(
                (float)((random.NextDouble() * 2) - 1),
                (float)((random.NextDouble() * 2) - 1)))
            .ToArray();
        var expected = new ComplexF[bins.Length * frames];
        var fullTransform = new ComplexF[fftSize];
        for (var frame = 0; frame < frames; frame++)
        {
            ScalarDft.Backward(input.AsSpan(frame * fftSize, fftSize), fullTransform);
            for (var binIndex = 0; binIndex < bins.Length; binIndex++)
            {
                expected[(binIndex * frames) + frame] = fullTransform[bins[binIndex]];
            }
        }

        var scalar = new ComplexF[expected.Length];
        var candidate = new PfbSelectedBinDft(fftSize, bins);
        candidate.TransformScalar(input, frames, scalar);
        AssertEquivalent(expected, scalar, 2e-5f);

        if (Avx2.IsSupported && Fma.IsSupported)
        {
            var avx2 = new ComplexF[scalar.Length];
            candidate.TransformAvx2(input, frames, avx2);
            AssertEquivalent(scalar, avx2, 2e-5f);
        }

        if (Avx512F.IsSupported)
        {
            var avx512 = new ComplexF[scalar.Length];
            candidate.TransformAvx512(input, frames, avx512);
            AssertEquivalent(scalar, avx512, 2e-5f);
        }
    }

    [Test]
    [NonParallelizable]
    public void SelectedBinKernelsDoNotAllocateInSteadyState()
    {
        if (!Avx512F.IsSupported)
        {
            Assert.Ignore("AVX-512F is not supported on this test host.");
        }

        const int fftSize = 64;
        const int frames = 128;
        var input = new ComplexF[fftSize * frames];
        var output = new ComplexF[frames * 2];
        var candidate = new PfbSelectedBinDft(fftSize, [0, 7]);
        candidate.TransformAvx512(input, frames, output);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            candidate.TransformAvx512(input, frames, output);
        }

        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    private static void AssertEquivalent(
        IReadOnlyList<ComplexF> expected,
        IReadOnlyList<ComplexF> actual,
        float tolerance)
    {
        Assert.That(actual, Has.Count.EqualTo(expected.Count));
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Multiple(() =>
            {
                Assert.That(actual[index].Real, Is.EqualTo(expected[index].Real).Within(tolerance), $"real[{index}]");
                Assert.That(actual[index].Imaginary, Is.EqualTo(expected[index].Imaginary).Within(tolerance), $"imag[{index}]");
            });
        }
    }
}
