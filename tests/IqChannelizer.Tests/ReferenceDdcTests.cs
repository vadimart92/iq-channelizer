using System.Numerics;
using IqChannelizer.Abstractions;
using IqChannelizer.Reference;

namespace IqChannelizer.Tests;

public sealed class ReferenceDdcTests
{
    [Test]
    public void DdcMixesInputRateToneToDcAndPreservesAbsoluteOrigin()
    {
        const double sampleRate = 1_024;
        const double frequency = 125;
        const long firstSample = 37;
        var input = DeterministicSignals.Tone(32, frequency, sampleRate, firstSample);

        var result = ReferenceDdc.Process(input, firstSample, sampleRate, frequency, [1d], 4);

        Assert.Multiple(() =>
        {
            Assert.That(result.Samples, Has.Length.EqualTo(8));
            Assert.That(result.Samples.All(sample => Complex.Abs(sample - Complex.One) < 4e-8), Is.True);
            Assert.That(result.FirstOutputInputSampleOffset, Is.EqualTo(new RationalSampleOffset(firstSample, 1)));
            Assert.That(result.InputSamplesPerOutputSample, Is.EqualTo(new RationalSampleOffset(4, 1)));
        });
    }

    [Test]
    public void DdcUsesCausalDoublePrecisionFirBeforeDecimation()
    {
        ComplexF[] input = [new(1, 2), new(2, 4), new(3, 6), new(4, 8), new(5, 10)];

        var result = ReferenceDdc.Process(input, 10, 100, 0, [0.25, 0.75], 2);

        Assert.That(result.Samples, Is.EqualTo(new[] { new Complex(1.25, 2.5), new Complex(3.25, 6.5) }));
        Assert.That(result.FirstOutputInputSampleOffset, Is.EqualTo(new RationalSampleOffset(21, 2)));
    }

    [Test]
    public void DdcPhaseSelectsTheFirstFullFirOutput()
    {
        var input = Enumerable.Range(1, 8).Select(value => new ComplexF(value, 0)).ToArray();

        var result = ReferenceDdc.Process(input, 0, 8, 0, [1d], 2, 1);

        Assert.That(result.Samples.Select(sample => sample.Real), Is.EqualTo(new[] { 2d, 4d, 6d, 8d }));
        Assert.That(result.FirstOutputInputSampleOffset, Is.EqualTo(new RationalSampleOffset(1, 1)));
    }

    [Test]
    public void DdcReturnsEmptyWhenInputCannotFillFir()
    {
        var result = ReferenceDdc.Process(new ComplexF[2], 0, 10, 0, [0.2, 0.3, 0.5], 2);
        Assert.That(result.Samples, Is.Empty);
    }

    [TestCase(0, 0, 1, 0)]
    [TestCase(10, double.NaN, 1, 0)]
    [TestCase(10, 0, 0, 0)]
    [TestCase(10, 0, 2, 2)]
    public void DdcRejectsInvalidParameters(double sampleRate, double frequency, int factor, int phase)
    {
        Assert.That(
            () => ReferenceDdc.Process(new ComplexF[4], 0, sampleRate, frequency, [1d], factor, phase),
            Throws.InstanceOf<ArgumentException>());
    }

    [Test]
    public void DdcRejectsEmptyOrNonFiniteTaps()
    {
        Assert.That(
            () => ReferenceDdc.Process(new ComplexF[4], 0, 10, 0, [], 1),
            Throws.ArgumentException);
        Assert.That(
            () => ReferenceDdc.Process(new ComplexF[4], 0, 10, 0, [double.NaN], 1),
            Throws.ArgumentException);
    }
}
