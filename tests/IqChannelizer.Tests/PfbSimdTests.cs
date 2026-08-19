using System.Runtime.Intrinsics.X86;
using IqChannelizer.Abstractions;
using IqChannelizer.Pfb;

namespace IqChannelizer.Tests;

public sealed class PfbSimdTests
{
    [TestCase(8, 3, 4, 5, 29)]
    [TestCase(16, 7, 8, 7, -11)]
    [TestCase(32, 13, 12, 6, 10_003)]
    [TestCase(16, 5, 16, 4, -10_003)]
    [TestCase(8, 4, 5, 6, 10_003)]
    [TestCase(16, 9, 20, 3, -29)]
    [TestCase(2, 1, 3, 5, 7)]
    public void PhaseParallelKernelMatchesScalarDirectRotatedStore(
        int fftSize,
        int hopSize,
        int tapsPerPhase,
        int frames,
        long firstNewSampleIndex)
    {
        if (!Avx2.IsSupported || !Fma.IsSupported)
        {
            Assert.Ignore("AVX2/FMA is not supported on this test host.");
        }

        var random = new Random(fftSize * 1000 + hopSize * 100 + frames);
        var prototype = Enumerable.Range(0, fftSize * tapsPerPhase)
            .Select(_ => (float)((random.NextDouble() - 0.5) / tapsPerPhase))
            .ToArray();
        var history = prototype.Length - 1;
        var inputLength = history + (hopSize * frames);
        var inputStorage = new ComplexF[inputLength + 1];
        for (var index = 0; index < inputLength; index++)
        {
            inputStorage[index + 1] = new ComplexF(
                (float)((random.NextDouble() * 2) - 1),
                (float)((random.NextDouble() * 2) - 1));
        }

        var scalarStorage = new ComplexF[(fftSize * frames) + 1];
        var avxStorage = new ComplexF[(fftSize * frames) + 2];
        var compactStorage = new ComplexF[(fftSize * frames) + 3];
        var genericStorage = new ComplexF[(fftSize * frames) + 4];
        var input = inputStorage.AsSpan(1, inputLength);
        var scalar = scalarStorage.AsSpan(1, fftSize * frames);
        var avx = avxStorage.AsSpan(2, fftSize * frames);
        var compact = compactStorage.AsSpan(3, fftSize * frames);
        var generic = genericStorage.AsSpan(4, fftSize * frames);
        var spanAbsoluteStart = firstNewSampleIndex - history;
        PfbPhaseFir.FillBatchScalar(
            input,
            spanAbsoluteStart,
            firstNewSampleIndex,
            hopSize,
            frames,
            fftSize,
            prototype,
            scalar);
        var coefficients = new Avx2PfbCoefficients(prototype, fftSize);
        PfbPhaseFir.FillBatchAvx2(
            input,
            spanAbsoluteStart,
            firstNewSampleIndex,
            hopSize,
            frames,
            prototype,
            coefficients,
            avx);
        PfbPhaseFir.FillBatchAvx2Generic(
            input,
            spanAbsoluteStart,
            firstNewSampleIndex,
            hopSize,
            frames,
            prototype,
            coefficients,
            generic);
        PfbPhaseFir.FillBatchAvx2Compact(
            input,
            spanAbsoluteStart,
            firstNewSampleIndex,
            hopSize,
            frames,
            fftSize,
            prototype,
            compact);

        for (var index = 0; index < scalar.Length; index++)
        {
            var scalarValue = scalar[index];
            var avxValue = avx[index];
            var compactValue = compact[index];
            var genericValue = generic[index];
            Assert.Multiple(() =>
            {
                Assert.That(avxValue.Real, Is.EqualTo(scalarValue.Real).Within(3e-6f), $"real[{index}]");
                Assert.That(avxValue.Imaginary, Is.EqualTo(scalarValue.Imaginary).Within(3e-6f), $"imag[{index}]");
                Assert.That(genericValue.Real, Is.EqualTo(scalarValue.Real).Within(3e-6f), $"generic real[{index}]");
                Assert.That(genericValue.Imaginary, Is.EqualTo(scalarValue.Imaginary).Within(3e-6f), $"generic imag[{index}]");
                Assert.That(compactValue.Real, Is.EqualTo(scalarValue.Real).Within(3e-6f), $"compact real[{index}]");
                Assert.That(compactValue.Imaginary, Is.EqualTo(scalarValue.Imaginary).Within(3e-6f), $"compact imag[{index}]");
            });
        }
    }

    [TestCase(4)]
    [TestCase(20)]
    [NonParallelizable]
    public void PhaseParallelKernelDoesNotAllocateInSteadyState(int tapsPerPhase)
    {
        if (!Avx2.IsSupported || !Fma.IsSupported)
        {
            Assert.Ignore("AVX2/FMA is not supported on this test host.");
        }

        const int fftSize = 8;
        const int hopSize = 3;
        const int frames = 5;
        var prototype = Enumerable.Repeat(1f / (fftSize * tapsPerPhase), fftSize * tapsPerPhase).ToArray();
        var history = prototype.Length - 1;
        var input = new ComplexF[history + (hopSize * frames)];
        var output = new ComplexF[fftSize * frames];
        var coefficients = new Avx2PfbCoefficients(prototype, fftSize);
        PfbPhaseFir.FillBatchAvx2(input, -history, 0, hopSize, frames, prototype, coefficients, output);
        PfbPhaseFir.FillBatchAvx2Generic(input, -history, 0, hopSize, frames, prototype, coefficients, output);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            PfbPhaseFir.FillBatchAvx2(input, -history, 0, hopSize, frames, prototype, coefficients, output);
            PfbPhaseFir.FillBatchAvx2Generic(input, -history, 0, hopSize, frames, prototype, coefficients, output);
        }

        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [TestCase(16, 7, 4, 5, 29)]
    [TestCase(32, 13, 8, 7, -11)]
    [TestCase(32, 11, 12, 6, 10_003)]
    [TestCase(16, 5, 16, 4, -10_003)]
    [TestCase(16, 8, 5, 6, 10_003)]
    [TestCase(32, 17, 20, 3, -29)]
    [TestCase(2, 1, 3, 5, 7)]
    public void Avx512PhaseParallelKernelMatchesScalarDirectRotatedStore(
        int fftSize,
        int hopSize,
        int tapsPerPhase,
        int frames,
        long firstNewSampleIndex)
    {
        if (!Avx512F.IsSupported)
        {
            Assert.Ignore("AVX-512F is not supported on this test host.");
        }

        var random = new Random(fftSize * 2000 + hopSize * 100 + frames);
        var prototype = Enumerable.Range(0, fftSize * tapsPerPhase)
            .Select(_ => (float)((random.NextDouble() - 0.5) / tapsPerPhase))
            .ToArray();
        var history = prototype.Length - 1;
        var inputLength = history + (hopSize * frames);
        var inputStorage = new ComplexF[inputLength + 1];
        for (var index = 0; index < inputLength; index++)
        {
            inputStorage[index + 1] = new ComplexF(
                (float)((random.NextDouble() * 2) - 1),
                (float)((random.NextDouble() * 2) - 1));
        }

        var scalarStorage = new ComplexF[(fftSize * frames) + 1];
        var avxStorage = new ComplexF[(fftSize * frames) + 2];
        var genericStorage = new ComplexF[(fftSize * frames) + 3];
        var input = inputStorage.AsSpan(1, inputLength);
        var scalar = scalarStorage.AsSpan(1, fftSize * frames);
        var avx = avxStorage.AsSpan(2, fftSize * frames);
        var generic = genericStorage.AsSpan(3, fftSize * frames);
        var spanAbsoluteStart = firstNewSampleIndex - history;
        PfbPhaseFir.FillBatchScalar(
            input,
            spanAbsoluteStart,
            firstNewSampleIndex,
            hopSize,
            frames,
            fftSize,
            prototype,
            scalar);
        var coefficients = new Avx512PfbCoefficients(prototype, fftSize);
        PfbPhaseFir.FillBatchAvx512(
            input,
            spanAbsoluteStart,
            firstNewSampleIndex,
            hopSize,
            frames,
            prototype,
            coefficients,
            avx);
        PfbPhaseFir.FillBatchAvx512Generic(
            input,
            spanAbsoluteStart,
            firstNewSampleIndex,
            hopSize,
            frames,
            prototype,
            coefficients,
            generic);

        for (var index = 0; index < scalar.Length; index++)
        {
            var scalarValue = scalar[index];
            var avxValue = avx[index];
            var genericValue = generic[index];
            Assert.Multiple(() =>
            {
                Assert.That(avxValue.Real, Is.EqualTo(scalarValue.Real).Within(3e-6f), $"real[{index}]");
                Assert.That(avxValue.Imaginary, Is.EqualTo(scalarValue.Imaginary).Within(3e-6f), $"imag[{index}]");
                Assert.That(genericValue.Real, Is.EqualTo(scalarValue.Real).Within(3e-6f), $"generic real[{index}]");
                Assert.That(genericValue.Imaginary, Is.EqualTo(scalarValue.Imaginary).Within(3e-6f), $"generic imag[{index}]");
            });
        }
    }

    [TestCase(4)]
    [TestCase(20)]
    [NonParallelizable]
    public void Avx512PhaseParallelKernelDoesNotAllocateInSteadyState(int tapsPerPhase)
    {
        if (!Avx512F.IsSupported)
        {
            Assert.Ignore("AVX-512F is not supported on this test host.");
        }

        const int fftSize = 16;
        const int hopSize = 7;
        const int frames = 5;
        var prototype = Enumerable.Repeat(1f / (fftSize * tapsPerPhase), fftSize * tapsPerPhase).ToArray();
        var history = prototype.Length - 1;
        var input = new ComplexF[history + (hopSize * frames)];
        var output = new ComplexF[fftSize * frames];
        var coefficients = new Avx512PfbCoefficients(prototype, fftSize);
        PfbPhaseFir.FillBatchAvx512(input, -history, 0, hopSize, frames, prototype, coefficients, output);
        PfbPhaseFir.FillBatchAvx512Generic(input, -history, 0, hopSize, frames, prototype, coefficients, output);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            PfbPhaseFir.FillBatchAvx512(input, -history, 0, hopSize, frames, prototype, coefficients, output);
            PfbPhaseFir.FillBatchAvx512Generic(input, -history, 0, hopSize, frames, prototype, coefficients, output);
        }

        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }
}
