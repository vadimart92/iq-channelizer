using System.Numerics;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Reference;

internal readonly record struct SampleTimeline(
    RationalSampleOffset FirstInputSampleOffset,
    RationalSampleOffset InputSamplesPerSample,
    int Count);

internal readonly record struct TimingAlignment(int FirstStartIndex, int SecondStartIndex, int Count);

internal readonly record struct SignalComparisonMetrics(
    double RmsComplexError,
    double MaxComplexError,
    double AmplitudeRatio,
    double MeanPhaseErrorRadians,
    double PhaseDriftRadiansPerSample,
    double LeakageRatio);

internal static class RationalTimingAligner
{
    public static TimingAlignment Align(SampleTimeline first, SampleTimeline second)
    {
        Validate(first, nameof(first));
        Validate(second, nameof(second));
        if (first.InputSamplesPerSample != second.InputSamplesPerSample)
        {
            throw new ArgumentException("Timelines must have the same rational sample stride.");
        }

        var firstIndex = 0;
        var secondIndex = 0;
        while (firstIndex < first.Count && secondIndex < second.Count)
        {
            var comparison = Compare(Position(first, firstIndex), Position(second, secondIndex));
            if (comparison == 0)
            {
                return new TimingAlignment(firstIndex, secondIndex, Math.Min(first.Count - firstIndex, second.Count - secondIndex));
            }

            if (comparison < 0)
            {
                firstIndex++;
            }
            else
            {
                secondIndex++;
            }
        }

        return new TimingAlignment(first.Count, second.Count, 0);
    }

    private static RationalSampleOffset Position(SampleTimeline timeline, int index)
    {
        var denominator = checked(timeline.FirstInputSampleOffset.Denominator * timeline.InputSamplesPerSample.Denominator);
        var numerator = checked(
            timeline.FirstInputSampleOffset.Numerator * timeline.InputSamplesPerSample.Denominator +
            (long)index * timeline.InputSamplesPerSample.Numerator * timeline.FirstInputSampleOffset.Denominator);
        return new RationalSampleOffset(numerator, denominator);
    }

    private static int Compare(RationalSampleOffset left, RationalSampleOffset right) =>
        checked(left.Numerator * right.Denominator).CompareTo(checked(right.Numerator * left.Denominator));

    private static void Validate(SampleTimeline timeline, string parameterName)
    {
        if (timeline.Count < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Timeline count cannot be negative.");
        }

        if (timeline.InputSamplesPerSample.Numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Timeline stride must be positive.");
        }
    }
}

internal static class SignalMetrics
{
    public static SignalComparisonMetrics Compare(ReadOnlySpan<Complex> expected, ReadOnlySpan<Complex> actual)
    {
        if (expected.IsEmpty || expected.Length != actual.Length)
        {
            throw new ArgumentException("Expected and actual signals must have the same non-zero length.");
        }

        double squaredError = 0;
        double maxError = 0;
        double expectedPower = 0;
        double actualPower = 0;
        var cross = Complex.Zero;
        var phases = new double[expected.Length];
        var previousPhase = 0d;

        for (var index = 0; index < expected.Length; index++)
        {
            EnsureFinite(expected[index], nameof(expected));
            EnsureFinite(actual[index], nameof(actual));
            var error = actual[index] - expected[index];
            var magnitude = error.Magnitude;
            squaredError += magnitude * magnitude;
            maxError = Math.Max(maxError, magnitude);
            expectedPower += expected[index].Magnitude * expected[index].Magnitude;
            actualPower += actual[index].Magnitude * actual[index].Magnitude;
            cross += actual[index] * Complex.Conjugate(expected[index]);

            var phase = expected[index] == Complex.Zero || actual[index] == Complex.Zero
                ? previousPhase
                : Math.Atan2((actual[index] * Complex.Conjugate(expected[index])).Imaginary,
                    (actual[index] * Complex.Conjugate(expected[index])).Real);
            if (index > 0)
            {
                while (phase - previousPhase > Math.PI) phase -= 2 * Math.PI;
                while (phase - previousPhase < -Math.PI) phase += 2 * Math.PI;
            }

            phases[index] = phase;
            previousPhase = phase;
        }

        if (expectedPower == 0)
        {
            throw new ArgumentException("Expected signal must contain non-zero energy.", nameof(expected));
        }

        var gain = cross / expectedPower;
        double residualPower = 0;
        for (var index = 0; index < expected.Length; index++)
        {
            var residual = actual[index] - gain * expected[index];
            residualPower += residual.Magnitude * residual.Magnitude;
        }

        var meanPhase = phases.Average();
        var center = (phases.Length - 1) / 2d;
        double phaseCovariance = 0;
        double indexVariance = 0;
        for (var index = 0; index < phases.Length; index++)
        {
            var centeredIndex = index - center;
            phaseCovariance += centeredIndex * (phases[index] - meanPhase);
            indexVariance += centeredIndex * centeredIndex;
        }

        return new SignalComparisonMetrics(
            Math.Sqrt(squaredError / expected.Length),
            maxError,
            gain.Magnitude,
            Math.Atan2(gain.Imaginary, gain.Real),
            indexVariance == 0 ? 0 : phaseCovariance / indexVariance,
            actualPower == 0 ? 0 : Math.Sqrt(residualPower / actualPower));
    }

    private static void EnsureFinite(Complex value, string parameterName)
    {
        if (!double.IsFinite(value.Real) || !double.IsFinite(value.Imaginary))
        {
            throw new ArgumentException("Signal samples must be finite.", parameterName);
        }
    }
}
