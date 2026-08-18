using System.Numerics;
using IqChannelizer.Abstractions;
using IqChannelizer.Reference;

namespace IqChannelizer.Tests;

public sealed class SignalToolkitTests
{
    [Test]
    public void ToneIsContinuousAcrossPartitionsAndSupportsOffBinFrequency()
    {
        var whole = DeterministicSignals.Tone(17, 123.25, 1_000, 11, 0.75, 0.2);
        var first = DeterministicSignals.Tone(6, 123.25, 1_000, 11, 0.75, 0.2);
        var second = DeterministicSignals.Tone(11, 123.25, 1_000, 17, 0.75, 0.2);

        Assert.That(first.Concat(second), Is.EqualTo(whole));
    }

    [Test]
    public void TwoToneAndBlockerAreExactSums()
    {
        var wanted = DeterministicSignals.Tone(16, 100, 1_024, 5, 0.25);
        var blocker = DeterministicSignals.Tone(16, -300, 1_024, 5, 4);
        var combined = DeterministicSignals.Blocker(16, 100, 0.25, -300, 4, 1_024, 5);

        for (var index = 0; index < combined.Length; index++)
        {
            Assert.That(combined[index], Is.EqualTo(wanted[index] + blocker[index]));
        }
    }

    [Test]
    public void ChirpEndpointsHaveRequestedInstantaneousFrequency()
    {
        const int count = 101;
        const double sampleRate = 2_000;
        var chirp = DeterministicSignals.LinearChirp(count, -200, 300, sampleRate);

        var firstStep = PhaseStep(chirp[0], chirp[1]) * sampleRate / (2 * Math.PI);
        var lastStep = PhaseStep(chirp[^2], chirp[^1]) * sampleRate / (2 * Math.PI);

        Assert.Multiple(() =>
        {
            Assert.That(firstStep, Is.EqualTo(-197.5).Within(2e-5));
            Assert.That(lastStep, Is.EqualTo(297.5).Within(2e-5));
        });
    }

    [Test]
    public void AmHasExpectedEnvelopeExtrema()
    {
        var signal = DeterministicSignals.Am(9, 0, 1, 0.5, 8);
        Assert.That(signal[0].Magnitude, Is.EqualTo(1.5).Within(1e-6));
        Assert.That(signal[4].Magnitude, Is.EqualTo(0.5).Within(1e-6));
    }

    [Test]
    public void SeededNoiseIsRepeatableAndSeedSensitive()
    {
        var first = DeterministicSignals.SeededNoise(32, 1234);
        var repeated = DeterministicSignals.SeededNoise(32, 1234);
        var different = DeterministicSignals.SeededNoise(32, 1235);

        Assert.That(repeated, Is.EqualTo(first));
        Assert.That(different, Is.Not.EqualTo(first));
    }

    [Test]
    public void ImpulseAndZerosHaveHandCheckableShapes()
    {
        var impulse = DeterministicSignals.Impulse(5, 3, new ComplexF(2, -1));
        Assert.That(impulse, Is.EqualTo(new[]
        {
            new ComplexF(), new ComplexF(), new ComplexF(), new ComplexF(2, -1), new ComplexF()
        }));
        Assert.That(DeterministicSignals.Zeros(3), Is.EqualTo(new ComplexF[3]));
    }

    [Test]
    public void GeneratorsRejectInvalidShapesAndFrequencies()
    {
        Assert.That(() => DeterministicSignals.Tone(-1, 0, 1), Throws.InstanceOf<ArgumentException>());
        Assert.That(() => DeterministicSignals.Tone(1, 0, 0), Throws.InstanceOf<ArgumentException>());
        Assert.That(() => DeterministicSignals.Tone(1, 6, 10), Throws.InstanceOf<ArgumentException>());
        Assert.That(() => DeterministicSignals.Impulse(0), Throws.InstanceOf<ArgumentException>());
        Assert.That(() => DeterministicSignals.Am(1, 0, 0, 1.1, 10), Throws.InstanceOf<ArgumentException>());
    }

    [Test]
    public void RationalAlignmentFindsSharedTimestampWithoutRounding()
    {
        var first = new SampleTimeline(new RationalSampleOffset(1, 2), new RationalSampleOffset(3, 2), 7);
        var second = new SampleTimeline(new RationalSampleOffset(7, 2), new RationalSampleOffset(3, 2), 4);

        var alignment = RationalTimingAligner.Align(first, second);

        Assert.That(alignment, Is.EqualTo(new TimingAlignment(2, 0, 4)));
    }

    [Test]
    public void RationalAlignmentReportsNoOverlapAndRejectsDifferentRates()
    {
        var early = new SampleTimeline(new RationalSampleOffset(0, 1), new RationalSampleOffset(1, 1), 2);
        var late = new SampleTimeline(new RationalSampleOffset(3, 1), new RationalSampleOffset(1, 1), 2);
        Assert.That(RationalTimingAligner.Align(early, late).Count, Is.Zero);

        var otherRate = new SampleTimeline(new RationalSampleOffset(0, 1), new RationalSampleOffset(2, 1), 2);
        Assert.That(() => RationalTimingAligner.Align(early, otherRate), Throws.ArgumentException);
    }

    [Test]
    public void MetricsMeasureAmplitudePhaseDriftAndLeakage()
    {
        var expected = Enumerable.Range(0, 64)
            .Select(index => Complex.FromPolarCoordinates(1, 0.13 * index))
            .ToArray();
        const double amplitude = 0.8;
        const double phase = 0.25;
        const double drift = 0.002;
        var actual = expected.Select((sample, index) =>
            sample * Complex.FromPolarCoordinates(amplitude, phase + drift * index)).ToArray();

        var metrics = SignalMetrics.Compare(expected, actual);

        Assert.Multiple(() =>
        {
            Assert.That(metrics.RmsComplexError, Is.GreaterThan(0));
            Assert.That(metrics.MaxComplexError, Is.GreaterThanOrEqualTo(metrics.RmsComplexError));
            Assert.That(metrics.AmplitudeRatio, Is.EqualTo(amplitude).Within(6e-4));
            Assert.That(metrics.MeanPhaseErrorRadians, Is.EqualTo(phase + drift * 31.5).Within(1e-6));
            Assert.That(metrics.PhaseDriftRadiansPerSample, Is.EqualTo(drift).Within(1e-12));
            Assert.That(metrics.LeakageRatio, Is.GreaterThan(0));
        });
    }

    [Test]
    public void MetricsAreExactForIdenticalSignalsAndRejectZeroReference()
    {
        Complex[] signal = [Complex.One, new(0, 1), new(-1, 0)];
        var metrics = SignalMetrics.Compare(signal, signal);

        Assert.Multiple(() =>
        {
            Assert.That(metrics.RmsComplexError, Is.Zero);
            Assert.That(metrics.MaxComplexError, Is.Zero);
            Assert.That(metrics.AmplitudeRatio, Is.EqualTo(1));
            Assert.That(metrics.MeanPhaseErrorRadians, Is.Zero);
            Assert.That(metrics.PhaseDriftRadiansPerSample, Is.Zero);
            Assert.That(metrics.LeakageRatio, Is.Zero);
        });
        Assert.That(() => SignalMetrics.Compare(new Complex[2], new Complex[2]), Throws.ArgumentException);
    }

    private static double PhaseStep(ComplexF first, ComplexF second)
    {
        var product = new Complex(second.Real, second.Imaginary) * Complex.Conjugate(new Complex(first.Real, first.Imaginary));
        return Math.Atan2(product.Imaginary, product.Real);
    }
}
