using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;

namespace IqChannelizer.Tests;

public sealed class ScalarPrimitiveTests
{
    [Test]
    public void ScalarFirUsesCausalNewestSampleConvention()
    {
        ComplexF[] input = [new(1, 2), new(2, 4), new(3, 6)];
        float[] taps = [0.25f, 0.75f];
        var output = new ComplexF[2];

        ScalarFir.Filter(input, taps, output);

        Assert.That(output, Is.EqualTo(new[] { new ComplexF(1.25f, 2.5f), new ComplexF(2.25f, 4.5f) }));
    }

    [Test]
    public void ScalarFirRejectsWrongOutputLength()
    {
        Assert.That(
            () => ScalarFir.Filter(new ComplexF[4], new float[2], new ComplexF[2]),
            Throws.ArgumentException);
        Assert.That(
            () => ScalarFir.Filter(new ComplexF[2], [float.NaN], new ComplexF[2]),
            Throws.ArgumentException);
    }

    [Test]
    public void ScalarFirAcceptsInsufficientInputWithEmptyOutput()
    {
        Assert.That(
            () => ScalarFir.Filter(new ComplexF[1], new float[2], Span<ComplexF>.Empty),
            Throws.Nothing);
    }

    [TestCase(0, new[] { 1, 3, 5, 7 })]
    [TestCase(1, new[] { 2, 4, 6, 8 })]
    public void PowerOfTwoDecimatorHasDeterministicPhase(int phase, int[] expected)
    {
        var input = Enumerable.Range(1, 8).Select(value => new ComplexF(value, -value)).ToArray();
        var output = new ComplexF[expected.Length];

        ScalarPowerOfTwoDecimator.Decimate(input, [1f], 2, phase, output);

        Assert.That(output.Select(value => (int)value.Real), Is.EqualTo(expected));
        Assert.That(output.Select(value => (int)value.Imaginary), Is.EqualTo(expected.Select(value => -value)));
    }

    [Test]
    public void PowerOfTwoDecimatorFactorOneIsFirIdentity()
    {
        ComplexF[] input = [new(1, -1), new(2, -2), new(3, -3)];
        var output = new ComplexF[3];
        ScalarPowerOfTwoDecimator.Decimate(input, [1f], 1, 0, output);
        Assert.That(output, Is.EqualTo(input));
    }

    [Test]
    public void PowerOfTwoDecimatorRejectsNonPowerOfTwoFactor()
    {
        Assert.That(
            () => ScalarPowerOfTwoDecimator.GetOutputCount(16, 3, 3, 0),
            Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(
            () => ScalarPowerOfTwoDecimator.GetOutputCount(16, 3, 2, 2),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void PowerOfTwoDecimatorFiltersBeforeSelectingPhase()
    {
        ComplexF[] input = [new(1, 0), new(2, 0), new(3, 0), new(4, 0), new(5, 0)];
        var output = new ComplexF[2];
        ScalarPowerOfTwoDecimator.Decimate(input, [0.5f, 0.5f], 2, 0, output);
        Assert.That(output.Select(value => value.Real), Is.EqualTo(new[] { 1.5f, 3.5f }));
    }

    [Test]
    public void SpectralSliceExtractorMapsPositiveThenNegativeOffsetsAtWrap()
    {
        var spectrum = Enumerable.Range(0, 8).Select(value => new ComplexF(value, 0)).ToArray();
        var destination = new ComplexF[4];

        SpectralSliceExtractor.Extract(spectrum, 0, [1f, 1f, 1f, 1f], new ComplexF(1, 0), destination);
        Assert.That(destination.Select(value => value.Real), Is.EqualTo(new[] { 0, 1, 2, 7 }));

        SpectralSliceExtractor.Extract(spectrum, 7, [1f, 1f, 1f, 1f], new ComplexF(1, 0), destination);
        Assert.That(destination.Select(value => value.Real), Is.EqualTo(new[] { 7, 0, 1, 6 }));

        SpectralSliceExtractor.Extract(spectrum, -1, [1f, 1f, 1f, 1f], new ComplexF(1, 0), destination);
        Assert.That(destination.Select(value => value.Real), Is.EqualTo(new[] { 7, 0, 1, 6 }));
    }

    [Test]
    public void SpectralSliceExtractorAppliesWindowAndComplexBlockPhase()
    {
        var spectrum = Enumerable.Range(0, 8).Select(value => new ComplexF(value, 0)).ToArray();
        var destination = new ComplexF[4];

        SpectralSliceExtractor.Extract(spectrum, 2, [1f, 2f, 3f, 4f], new ComplexF(0, 1), destination);

        Assert.That(destination, Is.EqualTo(new[]
        {
            new ComplexF(0, 2), new ComplexF(0, 6), new ComplexF(0, 12), new ComplexF(0, 4)
        }));
    }

    [Test]
    public void SpectralSliceExtractorRejectsInvalidShapes()
    {
        Assert.That(
            () => SpectralSliceExtractor.Extract(new ComplexF[8], 0, new float[3], new ComplexF(1, 0), new ComplexF[4]),
            Throws.ArgumentException);
        Assert.That(
            () => SpectralSliceExtractor.Extract(new ComplexF[4], 0, new float[5], new ComplexF(1, 0), new ComplexF[5]),
            Throws.ArgumentException);
    }

    [Test]
    [NonParallelizable]
    public void ScalarFirDecimatorAndExtractorDoNotAllocate()
    {
        var input = new ComplexF[64];
        float[] taps = [0.25f, 0.5f, 0.25f];
        var firOutput = new ComplexF[62];
        var decimated = new ComplexF[31];
        var spectrum = new ComplexF[64];
        var extracted = new ComplexF[16];
        var window = Enumerable.Repeat(1f, 16).ToArray();

        ScalarFir.Filter(input, taps, firOutput);
        ScalarPowerOfTwoDecimator.Decimate(input, taps, 2, 0, decimated);
        SpectralSliceExtractor.Extract(spectrum, 61, window, new ComplexF(1, 0), extracted);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var iteration = 0; iteration < 100; iteration++)
        {
            ScalarFir.Filter(input, taps, firOutput);
            ScalarPowerOfTwoDecimator.Decimate(input, taps, 2, 0, decimated);
            SpectralSliceExtractor.Extract(spectrum, 61, window, new ComplexF(1, 0), extracted);
        }

        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }
}
