using System.Numerics;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Reference;

internal sealed record ReferenceDdcResult(
    Complex[] Samples,
    RationalSampleOffset FirstOutputInputSampleOffset,
    RationalSampleOffset InputSamplesPerOutputSample);

internal static class ReferenceDdc
{
    public static ReferenceDdcResult Process(
        ReadOnlySpan<ComplexF> input,
        long firstInputSampleIndex,
        double inputSampleRateHz,
        double centerFrequencyHz,
        ReadOnlySpan<double> taps,
        int decimationFactor,
        int decimationPhase = 0)
    {
        Validate(inputSampleRateHz, centerFrequencyHz, taps, decimationFactor, decimationPhase);

        var firstNewestIndex = checked(taps.Length - 1 + decimationPhase);
        var outputCount = firstNewestIndex >= input.Length
            ? 0
            : 1 + ((input.Length - 1 - firstNewestIndex) / decimationFactor);
        var output = new Complex[outputCount];
        var angularFrequency = -2 * Math.PI * centerFrequencyHz / inputSampleRateHz;

        for (var outputIndex = 0; outputIndex < output.Length; outputIndex++)
        {
            var newestIndex = checked(firstNewestIndex + (outputIndex * decimationFactor));
            var sum = Complex.Zero;
            for (var tapIndex = 0; tapIndex < taps.Length; tapIndex++)
            {
                var inputIndex = newestIndex - tapIndex;
                var sample = input[inputIndex];
                var phase = angularFrequency * checked(firstInputSampleIndex + inputIndex);
                var oscillator = new Complex(Math.Cos(phase), Math.Sin(phase));
                sum += new Complex(sample.Real, sample.Imaginary) * oscillator * taps[tapIndex];
            }

            output[outputIndex] = sum;
        }

        // A symmetric FIR output is timestamped at the center of its input support.
        var firstOffsetNumerator = checked(
            (2 * firstInputSampleIndex) + (2L * firstNewestIndex) - (taps.Length - 1L));
        return new ReferenceDdcResult(
            output,
            new RationalSampleOffset(firstOffsetNumerator, 2),
            new RationalSampleOffset(decimationFactor, 1));
    }

    public static ReferenceDdcResult ProcessComplexTaps(
        ReadOnlySpan<ComplexF> input,
        long firstInputSampleIndex,
        double inputSampleRateHz,
        double centerFrequencyHz,
        ReadOnlySpan<Complex> taps,
        int decimationFactor,
        int decimationPhase = 0)
    {
        Validate(inputSampleRateHz, centerFrequencyHz, taps, decimationFactor, decimationPhase);

        var firstNewestIndex = checked(taps.Length - 1 + decimationPhase);
        var outputCount = firstNewestIndex >= input.Length
            ? 0
            : 1 + ((input.Length - 1 - firstNewestIndex) / decimationFactor);
        var output = new Complex[outputCount];
        var angularFrequency = -2 * Math.PI * centerFrequencyHz / inputSampleRateHz;
        for (var outputIndex = 0; outputIndex < output.Length; outputIndex++)
        {
            var newestIndex = checked(firstNewestIndex + (outputIndex * decimationFactor));
            var sum = Complex.Zero;
            for (var tapIndex = 0; tapIndex < taps.Length; tapIndex++)
            {
                var inputIndex = newestIndex - tapIndex;
                var sample = input[inputIndex];
                var phase = angularFrequency * checked(firstInputSampleIndex + inputIndex);
                var oscillator = new Complex(Math.Cos(phase), Math.Sin(phase));
                sum += new Complex(sample.Real, sample.Imaginary) * oscillator * taps[tapIndex];
            }

            output[outputIndex] = sum;
        }

        var firstOffsetNumerator = checked(
            (2 * firstInputSampleIndex) + (2L * firstNewestIndex) - (taps.Length - 1L));
        return new ReferenceDdcResult(
            output,
            new RationalSampleOffset(firstOffsetNumerator, 2),
            new RationalSampleOffset(decimationFactor, 1));
    }

    private static void Validate(
        double inputSampleRateHz,
        double centerFrequencyHz,
        ReadOnlySpan<double> taps,
        int decimationFactor,
        int decimationPhase)
    {
        if (!double.IsFinite(inputSampleRateHz) || inputSampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputSampleRateHz));
        }

        if (!double.IsFinite(centerFrequencyHz) || Math.Abs(centerFrequencyHz) > inputSampleRateHz / 2)
        {
            throw new ArgumentOutOfRangeException(nameof(centerFrequencyHz));
        }

        if (taps.IsEmpty || taps.ContainsAnyExceptInRange(double.MinValue, double.MaxValue))
        {
            throw new ArgumentException("Reference FIR taps must be non-empty and finite.", nameof(taps));
        }

        if (decimationFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(decimationFactor));
        }

        if ((uint)decimationPhase >= (uint)decimationFactor)
        {
            throw new ArgumentOutOfRangeException(nameof(decimationPhase));
        }
    }

    private static void Validate(
        double inputSampleRateHz,
        double centerFrequencyHz,
        ReadOnlySpan<Complex> taps,
        int decimationFactor,
        int decimationPhase)
    {
        if (!double.IsFinite(inputSampleRateHz) || inputSampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputSampleRateHz));
        }

        if (!double.IsFinite(centerFrequencyHz) || Math.Abs(centerFrequencyHz) > inputSampleRateHz / 2)
        {
            throw new ArgumentOutOfRangeException(nameof(centerFrequencyHz));
        }

        if (taps.IsEmpty)
        {
            throw new ArgumentException("Reference FIR taps must be non-empty and finite.", nameof(taps));
        }

        foreach (var tap in taps)
        {
            if (!double.IsFinite(tap.Real) || !double.IsFinite(tap.Imaginary))
            {
                throw new ArgumentException("Reference FIR taps must be non-empty and finite.", nameof(taps));
            }
        }

        if (decimationFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(decimationFactor));
        }

        if ((uint)decimationPhase >= (uint)decimationFactor)
        {
            throw new ArgumentOutOfRangeException(nameof(decimationPhase));
        }
    }
}
