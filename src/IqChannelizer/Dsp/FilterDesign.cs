using System.Collections.Concurrent;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Dsp;

public readonly record struct LowPassFilterSpec
{
    public LowPassFilterSpec(
        double InputSampleRateHz,
        double PassbandEdgeHz,
        double StopbandEdgeHz,
        double PassbandRippleDb,
        double StopbandAttenuationDb)
    {
        this.InputSampleRateHz = InputSampleRateHz;
        this.PassbandEdgeHz = PassbandEdgeHz;
        this.StopbandEdgeHz = StopbandEdgeHz;
        this.PassbandRippleDb = PassbandRippleDb;
        this.StopbandAttenuationDb = StopbandAttenuationDb;
        Validate();
    }

    public double InputSampleRateHz { get; }
    public double PassbandEdgeHz { get; }
    public double StopbandEdgeHz { get; }
    public double PassbandRippleDb { get; }
    public double StopbandAttenuationDb { get; }

    internal void Validate()
    {
        if (!double.IsFinite(InputSampleRateHz) || InputSampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InputSampleRateHz), "Input sample rate must be finite and positive.");
        }

        if (!double.IsFinite(PassbandEdgeHz) || PassbandEdgeHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PassbandEdgeHz), "Passband edge must be finite and positive.");
        }

        if (!double.IsFinite(StopbandEdgeHz) ||
            StopbandEdgeHz <= PassbandEdgeHz ||
            StopbandEdgeHz >= InputSampleRateHz / 2)
        {
            throw new ArgumentOutOfRangeException(nameof(StopbandEdgeHz), "Stopband edge must be finite, above the passband edge, and below Nyquist.");
        }

        if (!double.IsFinite(PassbandRippleDb) || PassbandRippleDb <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PassbandRippleDb), "Passband ripple must be finite and positive.");
        }

        if (!double.IsFinite(StopbandAttenuationDb) || StopbandAttenuationDb <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(StopbandAttenuationDb), "Stopband attenuation must be finite and positive.");
        }
    }
}

public sealed class DesignedLowPassFilter
{
    internal DesignedLowPassFilter(
        float[] taps,
        double normalizedPassbandEdge,
        double normalizedStopbandEdge,
        double passbandRippleDb,
        double stopbandAttenuationDb,
        double beta,
        double designMarginDb,
        FilterResponseMetrics achievedResponse)
    {
        Taps = taps;
        NormalizedPassbandEdge = normalizedPassbandEdge;
        NormalizedStopbandEdge = normalizedStopbandEdge;
        RequestedPassbandRippleDb = passbandRippleDb;
        RequestedStopbandAttenuationDb = stopbandAttenuationDb;
        Beta = beta;
        DesignMarginDb = designMarginDb;
        AchievedPassbandRippleDb = achievedResponse.PassbandRippleDb;
        AchievedStopbandAttenuationDb = achievedResponse.StopbandAttenuationDb;
        GroupDelayInputSamples = new RationalSampleOffset(Order, 2);
    }

    public ReadOnlyMemory<float> Taps { get; }
    public int Order => Taps.Length - 1;
    public double NormalizedPassbandEdge { get; }
    public double NormalizedStopbandEdge { get; }
    public double RequestedPassbandRippleDb { get; }
    public double RequestedStopbandAttenuationDb { get; }
    public double Beta { get; }
    public double DesignMarginDb { get; }
    public double AchievedPassbandRippleDb { get; }
    public double AchievedStopbandAttenuationDb { get; }
    public RationalSampleOffset GroupDelayInputSamples { get; }
}

public static class KaiserLowPassDesigner
{
    private const double DesignMarginDb = 3;
    private const int ResponseEvaluationPoints = 4097;
    private const int MaximumTapCount = 1_000_001;

    private readonly record struct NormalizedSpec(
        double PassbandEdge,
        double StopbandEdge,
        double PassbandRippleDb,
        double StopbandAttenuationDb);

    private static readonly ConcurrentDictionary<NormalizedSpec, DesignedLowPassFilter> Cache = new();

    public static DesignedLowPassFilter Design(LowPassFilterSpec specification)
    {
        specification.Validate();
        var normalized = new NormalizedSpec(
            specification.PassbandEdgeHz / specification.InputSampleRateHz,
            specification.StopbandEdgeHz / specification.InputSampleRateHz,
            specification.PassbandRippleDb,
            specification.StopbandAttenuationDb);
        return Cache.GetOrAdd(normalized, static key => DesignCore(key));
    }

    private static DesignedLowPassFilter DesignCore(NormalizedSpec specification)
    {
        var passbandDeviation = (Math.Pow(10, specification.PassbandRippleDb / 20) - 1) /
                                (Math.Pow(10, specification.PassbandRippleDb / 20) + 1);
        var stopbandDeviation = Math.Pow(10, -specification.StopbandAttenuationDb / 20);
        if (!double.IsFinite(passbandDeviation) || passbandDeviation <= 0 ||
            !double.IsFinite(stopbandDeviation) || stopbandDeviation <= 0)
        {
            throw new ArgumentException("The requested ripple or attenuation is outside the supported numerical range.");
        }

        var requiredAttenuation = -20 * Math.Log10(Math.Min(passbandDeviation, stopbandDeviation));
        var transitionRadians = 2 * Math.PI * (specification.StopbandEdge - specification.PassbandEdge);
        var normalizedCutoff = (specification.PassbandEdge + specification.StopbandEdge) / 2;
        for (var attempt = 0; attempt < 64; attempt++)
        {
            // Kaiser estimate: order ~= (A - 8) / (2.285 * normalized transition width in radians).
            var designAttenuation = requiredAttenuation + DesignMarginDb + attempt;
            var estimatedOrderValue = Math.Ceiling((designAttenuation - 8) / (2.285 * transitionRadians));
            if (!double.IsFinite(estimatedOrderValue) || estimatedOrderValue > MaximumTapCount - 1)
            {
                throw new ArgumentException("The requested transition requires more than one million FIR taps.");
            }

            var order = Math.Max(2, (int)estimatedOrderValue);
            if ((order & 1) != 0)
            {
                order++;
            }

            var beta = KaiserBeta(designAttenuation);
            var taps = GenerateTaps(order, normalizedCutoff, beta);
            var absoluteSpec = new LowPassFilterSpec(
                1,
                specification.PassbandEdge,
                specification.StopbandEdge,
                specification.PassbandRippleDb,
                specification.StopbandAttenuationDb);
            var measured = FrequencyResponseEvaluator.MeasureLowPass(taps, absoluteSpec, ResponseEvaluationPoints);
            if (measured.PassbandRippleDb <= specification.PassbandRippleDb + 1e-9 &&
                measured.StopbandAttenuationDb + 1e-9 >= specification.StopbandAttenuationDb)
            {
                return new DesignedLowPassFilter(
                    taps,
                    specification.PassbandEdge,
                    specification.StopbandEdge,
                    specification.PassbandRippleDb,
                    specification.StopbandAttenuationDb,
                    beta,
                    designAttenuation - requiredAttenuation,
                    measured);
            }
        }

        throw new ArgumentException("Kaiser refinement could not satisfy the requested response within 64 deterministic attempts.");
    }

    private static float[] GenerateTaps(int order, double cutoff, double beta)
    {
        var length = checked(order + 1);
        var center = order / 2;
        var denominator = ModifiedBesselI0(beta);
        var doubleTaps = new double[length];
        for (var index = 0; index <= center; index++)
        {
            var offset = index - (order / 2.0);
            var ideal = offset == 0
                ? 2 * cutoff
                : Math.Sin(2 * Math.PI * cutoff * offset) / (Math.PI * offset);
            var ratio = (2.0 * index / order) - 1;
            var window = ModifiedBesselI0(beta * Math.Sqrt(Math.Max(0, 1 - (ratio * ratio)))) / denominator;
            var tap = ideal * window;
            doubleTaps[index] = tap;
            doubleTaps[order - index] = tap;
        }

        var sum = doubleTaps.Sum();
        var taps = new float[length];
        for (var index = 0; index < length; index++)
        {
            taps[index] = (float)(doubleTaps[index] / sum);
        }

        var floatSum = taps.Sum(value => (double)value);
        taps[center] += (float)(1 - floatSum);
        return taps;
    }

    private static double KaiserBeta(double attenuationDb) => attenuationDb switch
    {
        > 50 => 0.1102 * (attenuationDb - 8.7),
        >= 21 => (0.5842 * Math.Pow(attenuationDb - 21, 0.4)) + (0.07886 * (attenuationDb - 21)),
        _ => 0
    };

    private static double ModifiedBesselI0(double value)
    {
        var halfSquared = value * value / 4;
        var sum = 1.0;
        var term = 1.0;
        for (var index = 1; index < 100; index++)
        {
            term *= halfSquared / (index * (double)index);
            sum += term;
            if (term <= sum * 1e-16)
            {
                break;
            }
        }

        return sum;
    }
}
