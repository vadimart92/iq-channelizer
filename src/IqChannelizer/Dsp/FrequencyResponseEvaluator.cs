namespace IqChannelizer.Dsp;

public readonly record struct ComplexResponse(double Real, double Imaginary)
{
    public double Magnitude => Math.Sqrt((Real * Real) + (Imaginary * Imaginary));
}

public readonly record struct FilterResponseMetrics(
    double PassbandRippleDb,
    double StopbandAttenuationDb,
    double MaximumPassbandMagnitude,
    double MinimumPassbandMagnitude,
    double MaximumStopbandMagnitude);

public sealed class DenseFrequencyResponse
{
    private readonly ComplexResponse[] _values;

    internal DenseFrequencyResponse(double sampleRateHz, ComplexResponse[] values)
    {
        SampleRateHz = sampleRateHz;
        _values = values;
    }

    public double SampleRateHz { get; }
    public double MinimumFrequencyHz => -SampleRateHz / 2;
    public double MaximumFrequencyHz => SampleRateHz / 2;
    public int SampleCount => _values.Length;
    public ReadOnlyMemory<ComplexResponse> Values => _values;

    internal double ConservativeMagnitudeAt(double frequencyHz)
    {
        var wrapped = WrapFrequency(frequencyHz, SampleRateHz);
        var position = (wrapped - MinimumFrequencyHz) * (SampleCount - 1) / SampleRateHz;
        var lower = Math.Clamp((int)Math.Floor(position), 0, SampleCount - 1);
        var upper = Math.Min(lower + 1, SampleCount - 1);
        return Math.Max(_values[lower].Magnitude, _values[upper].Magnitude);
    }

    private static double WrapFrequency(double frequencyHz, double sampleRateHz)
    {
        var wrapped = frequencyHz - (Math.Floor((frequencyHz + (sampleRateHz / 2)) / sampleRateHz) * sampleRateHz);
        return wrapped < -sampleRateHz / 2 ? wrapped + sampleRateHz : wrapped;
    }
}

public static class FrequencyResponseEvaluator
{
    public static ComplexResponse Evaluate(ReadOnlySpan<float> taps, double frequencyHz, double sampleRateHz)
    {
        ValidateTaps(taps);
        if (!double.IsFinite(sampleRateHz) || sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        if (!double.IsFinite(frequencyHz) || Math.Abs(frequencyHz) > sampleRateHz / 2)
        {
            throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        }

        return EvaluateUnchecked(taps, frequencyHz, sampleRateHz);
    }

    public static DenseFrequencyResponse EvaluateDense(ReadOnlySpan<float> taps, double sampleRateHz, int sampleCount)
    {
        ValidateTaps(taps);
        if (!double.IsFinite(sampleRateHz) || sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        if (sampleCount < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        }

        var values = new ComplexResponse[sampleCount];
        for (var index = 0; index < sampleCount; index++)
        {
            var frequency = (-sampleRateHz / 2) + (index * sampleRateHz / (sampleCount - 1));
            values[index] = EvaluateUnchecked(taps, frequency, sampleRateHz);
        }

        return new DenseFrequencyResponse(sampleRateHz, values);
    }

    public static FilterResponseMetrics MeasureLowPass(
        ReadOnlySpan<float> taps,
        LowPassFilterSpec specification,
        int sampleCount)
    {
        ValidateTaps(taps);
        specification.Validate();
        if (sampleCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        }

        var passbandMaximum = 0.0;
        var passbandMinimum = double.PositiveInfinity;
        var stopbandMaximum = 0.0;
        for (var index = 0; index < sampleCount; index++)
        {
            var passbandFrequency = index * specification.PassbandEdgeHz / (sampleCount - 1);
            var passbandMagnitude = EvaluateUnchecked(taps, passbandFrequency, specification.InputSampleRateHz).Magnitude;
            passbandMaximum = Math.Max(passbandMaximum, passbandMagnitude);
            passbandMinimum = Math.Min(passbandMinimum, passbandMagnitude);

            var stopbandFrequency = specification.StopbandEdgeHz +
                                    (index * ((specification.InputSampleRateHz / 2) - specification.StopbandEdgeHz) /
                                     (sampleCount - 1));
            stopbandMaximum = Math.Max(
                stopbandMaximum,
                EvaluateUnchecked(taps, stopbandFrequency, specification.InputSampleRateHz).Magnitude);
        }

        var ripple = passbandMinimum > 0
            ? 20 * Math.Log10(passbandMaximum / passbandMinimum)
            : double.PositiveInfinity;
        var attenuation = stopbandMaximum > 0
            ? -20 * Math.Log10(stopbandMaximum)
            : double.PositiveInfinity;
        return new FilterResponseMetrics(ripple, attenuation, passbandMaximum, passbandMinimum, stopbandMaximum);
    }

    private static ComplexResponse EvaluateUnchecked(ReadOnlySpan<float> taps, double frequencyHz, double sampleRateHz)
    {
        var radians = -2 * Math.PI * frequencyHz / sampleRateHz;
        double real = 0;
        double imaginary = 0;
        for (var index = 0; index < taps.Length; index++)
        {
            var phase = radians * index;
            real += taps[index] * Math.Cos(phase);
            imaginary += taps[index] * Math.Sin(phase);
        }

        return new ComplexResponse(real, imaginary);
    }

    private static void ValidateTaps(ReadOnlySpan<float> taps)
    {
        if (taps.IsEmpty)
        {
            throw new ArgumentException("At least one FIR tap is required.", nameof(taps));
        }

        foreach (var tap in taps)
        {
            if (!float.IsFinite(tap))
            {
                throw new ArgumentException("FIR taps must be finite.", nameof(taps));
            }
        }
    }
}
