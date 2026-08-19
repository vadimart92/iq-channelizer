using System.Runtime.Intrinsics.X86;
using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;

namespace IqChannelizer.Tests;

public sealed class SimdTests
{
    [Test]
    public void BackendResolverHonorsCapabilitiesAndForcedPreferences()
    {
        var scalarOnly = new SimdCapabilities(Avx2Fma: false, Avx512F: false);
        var avx2 = new SimdCapabilities(Avx2Fma: true, Avx512F: false);
        var avx512 = new SimdCapabilities(Avx2Fma: true, Avx512F: true);

        Assert.Multiple(() =>
        {
            Assert.That(SimdBackendResolver.Resolve(SimdPreference.Auto, scalarOnly), Is.EqualTo(SimdPreference.Scalar));
            Assert.That(SimdBackendResolver.Resolve(SimdPreference.Auto, avx2), Is.EqualTo(SimdPreference.Avx2));
            Assert.That(SimdBackendResolver.Resolve(SimdPreference.Auto, avx512), Is.EqualTo(SimdPreference.Avx512));
            Assert.That(
                SimdBackendResolver.Resolve(SimdPreference.Auto, avx512, autoPreferAvx512: false),
                Is.EqualTo(SimdPreference.Avx2));
            Assert.That(SimdBackendResolver.Resolve(SimdPreference.Scalar, avx2), Is.EqualTo(SimdPreference.Scalar));
            Assert.That(SimdBackendResolver.Resolve(SimdPreference.Avx2, avx2), Is.EqualTo(SimdPreference.Avx2));
            Assert.That(
                () => SimdBackendResolver.Resolve(SimdPreference.Avx2, scalarOnly),
                Throws.TypeOf<PlatformNotSupportedException>());
            Assert.That(
                () => SimdBackendResolver.Resolve(SimdPreference.Avx512, avx2),
                Throws.TypeOf<PlatformNotSupportedException>());
            Assert.That(SimdBackendResolver.Resolve(SimdPreference.Avx512, avx512), Is.EqualTo(SimdPreference.Avx512));
        });
    }

    [TestCase(1)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(17)]
    public void Avx2AosPrimitivesMatchScalarForTailsAndMisalignment(int length)
    {
        if (!Avx2.IsSupported || !Fma.IsSupported)
        {
            Assert.Ignore("AVX2/FMA is not supported on this test host.");
        }

        var random = new Random(0x51AD + length);
        var leftStorage = RandomComplex(random, length + 2);
        var rightStorage = RandomComplex(random, length + 3);
        var factorsStorage = Enumerable.Range(0, length + 4)
            .Select(_ => (float)((random.NextDouble() * 2) - 1))
            .ToArray();
        var destinationStorage = new ComplexF[length + 5];
        var left = leftStorage.AsSpan(1, length);
        var right = rightStorage.AsSpan(2, length);
        var factors = factorsStorage.AsSpan(3, length);
        var destination = destinationStorage.AsSpan(4, length);

        Avx2ComplexKernels.CopyScale(left, 0.37f, destination);
        AssertEquivalent(left.ToArray().Select(value => value * 0.37f), destination);

        Avx2ComplexKernels.MultiplyComplex(left, right, destination);
        AssertEquivalent(left.ToArray().Zip(right.ToArray(), (a, b) => a * b), destination);

        var scalar = new ComplexF(-0.2f, 0.7f);
        Avx2ComplexKernels.MultiplyComplexByScalar(left, scalar, destination);
        AssertEquivalent(left.ToArray().Select(value => value * scalar), destination);

        Avx2ComplexKernels.MultiplyComplexByReal(left, factors, destination);
        AssertEquivalent(left.ToArray().Zip(factors.ToArray(), (value, factor) => value * factor), destination);

        Avx2ComplexKernels.Add(left, right, destination);
        AssertEquivalent(left.ToArray().Zip(right.ToArray(), (a, b) => a + b), destination);
    }

    [Test]
    public void Avx2PrimitivesSupportExactInPlaceOperationAndRejectPartialOverlap()
    {
        if (!Avx2.IsSupported || !Fma.IsSupported)
        {
            Assert.Ignore("AVX2/FMA is not supported on this test host.");
        }

        var values = RandomComplex(new Random(17), 9);
        var expected = values.Select(value => value * 2f).ToArray();
        Avx2ComplexKernels.CopyScale(values, 2f, values);
        AssertEquivalent(expected, values);

        var overlapping = new ComplexF[10];
        Assert.That(
            () => Avx2ComplexKernels.CopyScale(overlapping.AsSpan(0, 8), 1, overlapping.AsSpan(1, 8)),
            Throws.ArgumentException);
    }

    [TestCase(1)]
    [TestCase(7)]
    [TestCase(8)]
    [TestCase(9)]
    [TestCase(17)]
    public void Avx512AosPrimitivesMatchScalarForTailsAndMisalignment(int length)
    {
        if (!Avx512F.IsSupported)
        {
            Assert.Ignore("AVX-512F is not supported on this test host.");
        }

        var random = new Random(0x5120 + length);
        var leftStorage = RandomComplex(random, length + 2);
        var rightStorage = RandomComplex(random, length + 3);
        var factorsStorage = Enumerable.Range(0, length + 4)
            .Select(_ => (float)((random.NextDouble() * 2) - 1))
            .ToArray();
        var destinationStorage = new ComplexF[length + 5];
        var left = leftStorage.AsSpan(1, length);
        var right = rightStorage.AsSpan(2, length);
        var factors = factorsStorage.AsSpan(3, length);
        var destination = destinationStorage.AsSpan(4, length);

        Avx512ComplexKernels.CopyScale(left, 0.37f, destination);
        AssertEquivalent(left.ToArray().Select(value => value * 0.37f), destination);
        Avx512ComplexKernels.MultiplyComplex(left, right, destination);
        AssertEquivalent(left.ToArray().Zip(right.ToArray(), (a, b) => a * b), destination);
        var scalar = new ComplexF(-0.2f, 0.7f);
        Avx512ComplexKernels.MultiplyComplexByScalar(left, scalar, destination);
        AssertEquivalent(left.ToArray().Select(value => value * scalar), destination);
        Avx512ComplexKernels.MultiplyComplexByReal(left, factors, destination);
        AssertEquivalent(left.ToArray().Zip(factors.ToArray(), (value, factor) => value * factor), destination);
        Avx512ComplexKernels.Add(left, right, destination);
        AssertEquivalent(left.ToArray().Zip(right.ToArray(), (a, b) => a + b), destination);
    }

    [Test]
    public void Avx512PrimitivesSupportExactInPlaceOperationAndRejectPartialOverlap()
    {
        if (!Avx512F.IsSupported)
        {
            Assert.Ignore("AVX-512F is not supported on this test host.");
        }

        var values = RandomComplex(new Random(512), 17);
        var expected = values.Select(value => value * 2f).ToArray();
        Avx512ComplexKernels.CopyScale(values, 2f, values);
        AssertEquivalent(expected, values);

        var overlapping = new ComplexF[18];
        Assert.That(
            () => Avx512ComplexKernels.CopyScale(overlapping.AsSpan(0, 16), 1, overlapping.AsSpan(1, 16)),
            Throws.ArgumentException);
    }

    [TestCase(0, 7)]
    [TestCase(15, 8)]
    [TestCase(-3, 13)]
    [TestCase(61, 17)]
    public void Avx2SpectralExtractionMatchesScalarAcrossWrapAndTails(int centerBin, int length)
    {
        if (!Avx2.IsSupported || !Fma.IsSupported)
        {
            Assert.Ignore("AVX2/FMA is not supported on this test host.");
        }

        var random = new Random(3100 + centerBin + length);
        var spectrumStorage = RandomComplex(random, 67);
        var windowStorage = RandomComplex(random, length + 1);
        var scalarStorage = new ComplexF[length + 2];
        var avxStorage = new ComplexF[length + 3];
        var spectrum = spectrumStorage.AsSpan(1, 64);
        var window = windowStorage.AsSpan(1, length);
        var scalar = scalarStorage.AsSpan(1, length);
        var avx = avxStorage.AsSpan(2, length);
        var phase = new ComplexF(-0.37f, 0.81f);

        SpectralSliceExtractor.Extract(spectrum, centerBin, window, phase, scalar);
        SpectralSliceExtractor.ExtractAvx2(spectrum, centerBin, window, phase, avx);
        AssertEquivalent(scalar.ToArray(), avx);
    }

    [TestCase(0, 7)]
    [TestCase(15, 8)]
    [TestCase(-3, 13)]
    [TestCase(61, 17)]
    public void Avx512SpectralExtractionMatchesScalarAcrossWrapAndTails(int centerBin, int length)
    {
        if (!Avx512F.IsSupported)
        {
            Assert.Ignore("AVX-512F is not supported on this test host.");
        }

        var random = new Random(5100 + centerBin + length);
        var spectrumStorage = RandomComplex(random, 67);
        var windowStorage = RandomComplex(random, length + 1);
        var scalarStorage = new ComplexF[length + 2];
        var avxStorage = new ComplexF[length + 3];
        var spectrum = spectrumStorage.AsSpan(1, 64);
        var window = windowStorage.AsSpan(1, length);
        var scalar = scalarStorage.AsSpan(1, length);
        var avx = avxStorage.AsSpan(2, length);
        var phase = new ComplexF(-0.37f, 0.81f);

        SpectralSliceExtractor.Extract(spectrum, centerBin, window, phase, scalar);
        SpectralSliceExtractor.ExtractAvx512(spectrum, centerBin, window, phase, avx);
        AssertEquivalent(scalar.ToArray(), avx);
    }

    [TestCase(1)]
    [TestCase(7)]
    [TestCase(1025)]
    public void Avx2ResidualRotatorMatchesScalarAcrossReanchorAndLargeOrigin(int length)
    {
        if (!Avx2.IsSupported || !Fma.IsSupported)
        {
            Assert.Ignore("AVX2/FMA is not supported on this test host.");
        }

        var scalar = RandomComplex(new Random(91 + length), length);
        var avx = scalar.ToArray();
        const long first = (1L << 53) + 12_345;
        ScalarRotator.RotateInPlace(scalar, 123_456.75, 1_000_000, first, 7);
        ScalarRotator.RotateInPlaceAvx2(avx, 123_456.75, 1_000_000, first, 7);
        AssertEquivalent(scalar, avx);
    }

    private static ComplexF[] RandomComplex(Random random, int length) => Enumerable.Range(0, length)
        .Select(_ => new ComplexF(
            (float)((random.NextDouble() * 2) - 1),
            (float)((random.NextDouble() * 2) - 1)))
        .ToArray();

    private static void AssertEquivalent(IEnumerable<ComplexF> expectedValues, ReadOnlySpan<ComplexF> actual)
    {
        var expected = expectedValues.ToArray();
        Assert.That(actual.Length, Is.EqualTo(expected.Length));
        for (var index = 0; index < actual.Length; index++)
        {
            var actualValue = actual[index];
            var expectedValue = expected[index];
            Assert.Multiple(() =>
            {
                Assert.That(actualValue.Real, Is.EqualTo(expectedValue.Real).Within(2e-6f), $"real[{index}]");
                Assert.That(actualValue.Imaginary, Is.EqualTo(expectedValue.Imaginary).Within(2e-6f), $"imag[{index}]");
            });
        }
    }
}
