using IqChannelizer.Abstractions;

namespace IqChannelizer.Dsp;

internal static class ScalarRotator
{
    private const int ReanchorInterval = 1024;

    public static void RotateInPlace(
        Span<ComplexF> samples,
        double frequencyHz,
        double inputSampleRateHz,
        long firstAbsoluteInputSampleIndex,
        int inputSamplesPerOutputSample)
    {
        if (frequencyHz == 0 || samples.IsEmpty)
        {
            return;
        }

        var phasor = CreateDoublePhasor(
            frequencyHz,
            inputSampleRateHz,
            firstAbsoluteInputSampleIndex);
        var step = CreateDoublePhasor(
            frequencyHz,
            inputSampleRateHz,
            inputSamplesPerOutputSample);
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = phasor.Multiply(samples[index]);
            phasor *= step;
            if ((index & (ReanchorInterval - 1)) == ReanchorInterval - 1)
            {
                var nextAbsoluteIndex = checked(
                    firstAbsoluteInputSampleIndex + ((long)(index + 1) * inputSamplesPerOutputSample));
                phasor = CreateDoublePhasor(frequencyHz, inputSampleRateHz, nextAbsoluteIndex);
            }
        }
    }

    internal static ComplexF CreatePhasor(double frequencyHz, double sampleRateHz, long absoluteSampleIndex)
    {
        var phasor = CreateDoublePhasor(frequencyHz, sampleRateHz, absoluteSampleIndex);
        return new ComplexF((float)phasor.Real, (float)phasor.Imaginary);
    }

    private static DoublePhasor CreateDoublePhasor(
        double frequencyHz,
        double sampleRateHz,
        long absoluteSampleIndex)
    {
        var fractionalCycles = FractionalCyclesAt(frequencyHz, sampleRateHz, absoluteSampleIndex);
        var (sine, cosine) = Math.SinCos(-2 * Math.PI * fractionalCycles);
        return new DoublePhasor(cosine, sine);
    }

    private static double FractionalCyclesAt(
        double frequencyHz,
        double sampleRateHz,
        long absoluteSampleIndex)
    {
        frequencyHz = Math.IEEERemainder(frequencyHz, sampleRateHz);
        var negative = absoluteSampleIndex < 0;
        var magnitude = negative
            ? (ulong)(-(absoluteSampleIndex + 1)) + 1
            : (ulong)absoluteSampleIndex;
        var remainderHz = 0d;
        for (var shift = 48; shift >= 0; shift -= 16)
        {
            var digit = (magnitude >> shift) & 0xffffUL;
            remainderHz = Math.IEEERemainder(
                (remainderHz * 65_536) + (frequencyHz * digit),
                sampleRateHz);
        }

        var fractionalCycles = remainderHz / sampleRateHz;
        return negative ? -fractionalCycles : fractionalCycles;
    }

    private readonly record struct DoublePhasor(double Real, double Imaginary)
    {
        public static DoublePhasor operator *(DoublePhasor left, DoublePhasor right) => new(
            (left.Real * right.Real) - (left.Imaginary * right.Imaginary),
            (left.Real * right.Imaginary) + (left.Imaginary * right.Real));

        public ComplexF Multiply(ComplexF value) => new(
            (float)((value.Real * Real) - (value.Imaginary * Imaginary)),
            (float)((value.Real * Imaginary) + (value.Imaginary * Real)));
    }
}
