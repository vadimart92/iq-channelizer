using System.Numerics;

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
    public double FrequencyStepHz => SampleRateHz / (SampleCount - 1);
    public ReadOnlyMemory<ComplexResponse> Values => _values;

    internal double ConservativeMagnitudeAt(double frequencyHz)
    {
        var wrapped = WrapFrequency(frequencyHz, SampleRateHz);
        var position = (wrapped - MinimumFrequencyHz) * (SampleCount - 1) / SampleRateHz;
        var lower = Math.Clamp((int)Math.Floor(position), 0, SampleCount - 1);
        var upper = Math.Min(lower + 1, SampleCount - 1);
        var previous = Math.Max(0, lower - 1);
        var next = Math.Min(SampleCount - 1, upper + 1);
        return Math.Max(
            Math.Max(_values[previous].Magnitude, _values[lower].Magnitude),
            Math.Max(_values[upper].Magnitude, _values[next].Magnitude));
    }

    private static double WrapFrequency(double frequencyHz, double sampleRateHz)
    {
        var wrapped = frequencyHz - (Math.Floor((frequencyHz + (sampleRateHz / 2)) / sampleRateHz) * sampleRateHz);
        return wrapped < -sampleRateHz / 2 ? wrapped + sampleRateHz : wrapped;
    }
}

public static class FrequencyResponseEvaluator
{
    private const int ConservativeSamplesPerTap = 8;
    private const int MaximumConservativeIntervalCount = 1 << 23;

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

        var intervalCount = sampleCount - 1;
        if ((intervalCount & (intervalCount - 1)) == 0)
        {
            return EvaluateUniformPowerOfTwo(taps, sampleRateHz, intervalCount);
        }

        var values = new ComplexResponse[sampleCount];
        for (var index = 0; index < sampleCount; index++)
        {
            var frequency = (-sampleRateHz / 2) + (index * sampleRateHz / intervalCount);
            values[index] = EvaluateUnchecked(taps, frequency, sampleRateHz);
        }
        return new DenseFrequencyResponse(sampleRateHz, values);
    }

    internal static DenseFrequencyResponse EvaluateDenseConservative(
        ReadOnlySpan<float> taps,
        double sampleRateHz,
        int minimumSampleCount)
    {
        ValidateTaps(taps);
        if (minimumSampleCount < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumSampleCount));
        }

        var minimumIntervals = minimumSampleCount - 1;
        var tapIntervals = checked(Math.Max(1, taps.Length - 1) * ConservativeSamplesPerTap);
        var requiredIntervals = Math.Max(minimumIntervals, tapIntervals);
        var intervalCount = NextPowerOfTwo(requiredIntervals);
        if (intervalCount > MaximumConservativeIntervalCount)
        {
            throw new ArgumentException(
                $"Conservative response validation requires more than {MaximumConservativeIntervalCount} frequency intervals for this FIR.");
        }

        return EvaluateDense(taps, sampleRateHz, checked(intervalCount + 1));
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

        var response = EvaluateDenseConservative(taps, specification.InputSampleRateHz, sampleCount);
        var passbandMaximum = 0d;
        var passbandMinimum = double.PositiveInfinity;
        var stopbandMaximum = 0d;
        for (var index = 0; index < response.SampleCount; index++)
        {
            var frequency = response.MinimumFrequencyHz + (index * response.FrequencyStepHz);
            var magnitude = response.Values.Span[index].Magnitude;
            var absoluteFrequency = Math.Abs(frequency);
            if (absoluteFrequency <= specification.PassbandEdgeHz)
            {
                passbandMaximum = Math.Max(passbandMaximum, magnitude);
                passbandMinimum = Math.Min(passbandMinimum, magnitude);
            }

            if (absoluteFrequency >= specification.StopbandEdgeHz)
            {
                stopbandMaximum = Math.Max(stopbandMaximum, magnitude);
            }
        }

        IncludePassbandMagnitude(
            EvaluateUnchecked(taps, specification.PassbandEdgeHz, specification.InputSampleRateHz).Magnitude,
            ref passbandMinimum,
            ref passbandMaximum);
        stopbandMaximum = Math.Max(
            stopbandMaximum,
            EvaluateUnchecked(taps, specification.StopbandEdgeHz, specification.InputSampleRateHz).Magnitude);

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

    private static DenseFrequencyResponse EvaluateUniformPowerOfTwo(
        ReadOnlySpan<float> taps,
        double sampleRateHz,
        int intervalCount)
    {
        var spectrum = new Complex[intervalCount];
        for (var index = 0; index < taps.Length; index++)
        {
            spectrum[index & (intervalCount - 1)] += taps[index];
        }

        ForwardFftInPlace(spectrum);
        var values = new ComplexResponse[checked(intervalCount + 1)];
        var half = intervalCount / 2;
        for (var index = 0; index <= intervalCount; index++)
        {
            var signedBin = index - half;
            var bin = signedBin < 0 ? signedBin + intervalCount : signedBin;
            if (bin == intervalCount)
            {
                bin = 0;
            }

            var value = spectrum[bin];
            values[index] = new ComplexResponse(value.Real, value.Imaginary);
        }

        return new DenseFrequencyResponse(sampleRateHz, values);
    }

    private static void ForwardFftInPlace(Span<Complex> values)
    {
        for (var index = 1; index < values.Length; index++)
        {
            var reversed = ReverseBits(index, values.Length);
            if (reversed > index)
            {
                (values[index], values[reversed]) = (values[reversed], values[index]);
            }
        }

        for (var length = 2; length <= values.Length; length <<= 1)
        {
            var root = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            var half = length / 2;
            for (var start = 0; start < values.Length; start += length)
            {
                var twiddle = Complex.One;
                for (var offset = 0; offset < half; offset++)
                {
                    var even = values[start + offset];
                    var odd = values[start + offset + half] * twiddle;
                    values[start + offset] = even + odd;
                    values[start + offset + half] = even - odd;
                    twiddle *= root;
                }
            }

            if (length == values.Length)
            {
                break;
            }
        }
    }

    private static int ReverseBits(int value, int length)
    {
        var reversed = 0;
        for (var remaining = length; remaining > 1; remaining >>= 1)
        {
            reversed = (reversed << 1) | (value & 1);
            value >>= 1;
        }

        return reversed;
    }

    private static int NextPowerOfTwo(int value)
    {
        if (value <= 1)
        {
            return 1;
        }

        var result = 1;
        while (result < value)
        {
            if (result > MaximumConservativeIntervalCount / 2)
            {
                return MaximumConservativeIntervalCount + 1;
            }

            result <<= 1;
        }

        return result;
    }

    private static void IncludePassbandMagnitude(double magnitude, ref double minimum, ref double maximum)
    {
        minimum = Math.Min(minimum, magnitude);
        maximum = Math.Max(maximum, magnitude);
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
