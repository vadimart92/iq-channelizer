using IqChannelizer.Dsp;

namespace IqChannelizer.Tests;

public sealed class FilterDesignTests
{
    [TestCase(0, 1_000, 2_000, 0.1, 60)]
    [TestCase(48_000, 0, 2_000, 0.1, 60)]
    [TestCase(48_000, 3_000, 2_000, 0.1, 60)]
    [TestCase(48_000, 2_000, 24_000, 0.1, 60)]
    [TestCase(48_000, 2_000, 3_000, 0, 60)]
    [TestCase(48_000, 2_000, 3_000, 0.1, double.NaN)]
    public void InvalidLowPassSpecificationsAreRejected(
        double sampleRate,
        double passband,
        double stopband,
        double ripple,
        double attenuation)
    {
        Assert.That(
            () => _ = new LowPassFilterSpec(sampleRate, passband, stopband, ripple, attenuation),
            Throws.InstanceOf<ArgumentException>());
    }

    [Test]
    public void KaiserDesignIsSymmetricNormalizedAndCachedByNormalizedSpec()
    {
        var first = KaiserLowPassDesigner.Design(new LowPassFilterSpec(48_000, 5_000, 8_000, 0.2, 50));
        var scaled = KaiserLowPassDesigner.Design(new LowPassFilterSpec(96_000, 10_000, 16_000, 0.2, 50));
        var taps = first.Taps.ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(scaled, Is.SameAs(first));
            Assert.That(taps.Length, Is.GreaterThan(3));
            Assert.That(taps.Length % 2, Is.EqualTo(1));
            Assert.That(first.Order, Is.EqualTo(taps.Length - 1));
            Assert.That(first.GroupDelayInputSamples.Numerator, Is.EqualTo(first.Order / 2));
            Assert.That(first.GroupDelayInputSamples.Denominator, Is.EqualTo(1));
            Assert.That(taps.Sum(value => (double)value), Is.EqualTo(1).Within(2e-7));
        });

        for (var index = 0; index < taps.Length; index++)
        {
            Assert.That(taps[index], Is.EqualTo(taps[^(index + 1)]).Within(1e-7), $"tap {index}");
        }
    }

    [Test]
    public void KaiserDesignMeetsRequestedStandaloneResponse()
    {
        var spec = new LowPassFilterSpec(48_000, 5_000, 8_000, 0.2, 55);
        var design = KaiserLowPassDesigner.Design(spec);
        var measured = FrequencyResponseEvaluator.MeasureLowPass(design.Taps.Span, spec, 4097);

        Assert.Multiple(() =>
        {
            Assert.That(measured.PassbandRippleDb, Is.LessThanOrEqualTo(spec.PassbandRippleDb));
            Assert.That(measured.StopbandAttenuationDb, Is.GreaterThanOrEqualTo(spec.StopbandAttenuationDb));
            Assert.That(design.AchievedPassbandRippleDb, Is.LessThanOrEqualTo(spec.PassbandRippleDb));
            Assert.That(design.AchievedStopbandAttenuationDb, Is.GreaterThanOrEqualTo(spec.StopbandAttenuationDb));
        });
    }

    [TestCase(48_000, 6_000, 9_000, 0.1, 60)]
    [TestCase(96_000, 8_000, 14_000, 0.05, 70)]
    [TestCase(1_000_000, 50_000, 80_000, 0.25, 45)]
    public void KaiserDesignMeetsSeveralNormalizedSpecifications(
        double sampleRate,
        double passband,
        double stopband,
        double ripple,
        double attenuation)
    {
        var spec = new LowPassFilterSpec(sampleRate, passband, stopband, ripple, attenuation);
        var design = KaiserLowPassDesigner.Design(spec);
        var measured = FrequencyResponseEvaluator.MeasureLowPass(design.Taps.Span, spec, 4097);

        Assert.Multiple(() =>
        {
            Assert.That(measured.PassbandRippleDb, Is.LessThanOrEqualTo(ripple));
            Assert.That(measured.StopbandAttenuationDb, Is.GreaterThanOrEqualTo(attenuation));
            Assert.That(design.DesignMarginDb, Is.GreaterThanOrEqualTo(3));
        });
    }

    [Test]
    public void KaiserDesignerRejectsNumericallyUnrepresentableAttenuation()
    {
        var spec = new LowPassFilterSpec(48_000, 5_000, 8_000, 0.1, 10_000);
        Assert.That(() => KaiserLowPassDesigner.Design(spec), Throws.ArgumentException);
    }

    [Test]
    public void FrequencyResponseHasHandCheckableTwoTapValues()
    {
        float[] taps = [0.5f, 0.5f];
        var dc = FrequencyResponseEvaluator.Evaluate(taps, 0, 8_000);
        var quarterRate = FrequencyResponseEvaluator.Evaluate(taps, 2_000, 8_000);
        var nyquist = FrequencyResponseEvaluator.Evaluate(taps, 4_000, 8_000);

        Assert.Multiple(() =>
        {
            Assert.That(dc.Real, Is.EqualTo(1).Within(1e-12));
            Assert.That(dc.Imaginary, Is.Zero.Within(1e-12));
            Assert.That(quarterRate.Magnitude, Is.EqualTo(Math.Sqrt(0.5)).Within(1e-12));
            Assert.That(nyquist.Magnitude, Is.Zero.Within(1e-12));
        });
    }

    [Test]
    public void FrequencyResponseRejectsInvalidInputs()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => FrequencyResponseEvaluator.Evaluate([], 0, 8_000), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => FrequencyResponseEvaluator.Evaluate([1f], double.NaN, 8_000), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => FrequencyResponseEvaluator.EvaluateDense([1f], 8_000, 2), Throws.InstanceOf<ArgumentException>());
        });
    }

    [Test]
    public void DenseResponseCoversSignedNyquistInterval()
    {
        var response = FrequencyResponseEvaluator.EvaluateDense([1f], 8_000, 17);

        Assert.Multiple(() =>
        {
            Assert.That(response.MinimumFrequencyHz, Is.EqualTo(-4_000));
            Assert.That(response.MaximumFrequencyHz, Is.EqualTo(4_000));
            Assert.That(response.SampleCount, Is.EqualTo(17));
            Assert.That(response.Values.Span.ToArray().All(value => Math.Abs(value.Magnitude - 1) < 1e-12), Is.True);
        });
    }

    [Test]
    public void ConservativeAliasEvaluatorSumsEveryFoldedImageMagnitude()
    {
        var response = FrequencyResponseEvaluator.EvaluateDense([1f], 48_000, 4097);
        var result = AliasedResponseEvaluator.EvaluateConservative(response, 4, 2_000, 257);

        Assert.Multiple(() =>
        {
            Assert.That(result.AliasImageCount, Is.EqualTo(3));
            Assert.That(result.WorstAliasMagnitude, Is.EqualTo(3).Within(1e-12));
            Assert.That(result.WorstAliasAttenuationDb, Is.EqualTo(-20 * Math.Log10(3)).Within(1e-12));
        });
    }

    [Test]
    public void DesignedFilterHasLowConservativeFoldedLeakageForDecimationByTwo()
    {
        var spec = new LowPassFilterSpec(48_000, 5_000, 8_000, 0.2, 55);
        var design = KaiserLowPassDesigner.Design(spec);
        var response = FrequencyResponseEvaluator.EvaluateDense(design.Taps.Span, spec.InputSampleRateHz, 16385);
        var folded = AliasedResponseEvaluator.EvaluateConservative(response, 2, spec.PassbandEdgeHz, 2049);

        Assert.That(folded.WorstAliasAttenuationDb, Is.GreaterThanOrEqualTo(50));
    }

    [Test]
    public void AliasEvaluatorRejectsPassbandAtOrBeyondOutputNyquist()
    {
        var response = FrequencyResponseEvaluator.EvaluateDense([1f], 48_000, 257);
        Assert.That(
            () => AliasedResponseEvaluator.EvaluateConservative(response, 4, 6_000, 33),
            Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(
            () => AliasedResponseEvaluator.EvaluateConservative(response, 0, 1_000, 33),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }
}
