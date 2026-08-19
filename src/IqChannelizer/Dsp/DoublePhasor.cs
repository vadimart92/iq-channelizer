using System.Runtime.CompilerServices;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Dsp;

internal readonly record struct DoublePhasor(double Real, double Imaginary)
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DoublePhasor operator *(DoublePhasor left, DoublePhasor right) => new(
        (left.Real * right.Real) - (left.Imaginary * right.Imaginary),
        (left.Real * right.Imaginary) + (left.Imaginary * right.Real));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DoublePhasor Normalize()
    {
        var magnitude = Math.Sqrt((Real * Real) + (Imaginary * Imaginary));
        return new DoublePhasor(Real / magnitude, Imaginary / magnitude);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DoublePhasor Pow(int exponent)
    {
        var result = new DoublePhasor(1, 0);
        var factor = this;
        while (exponent > 0)
        {
            if ((exponent & 1) != 0)
            {
                result *= factor;
            }

            factor *= factor;
            exponent >>= 1;
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ComplexF Multiply(ComplexF value) => new(
        (float)((value.Real * Real) - (value.Imaginary * Imaginary)),
        (float)((value.Real * Imaginary) + (value.Imaginary * Real)));
    
    internal static DoublePhasor Create(
        double frequencyHz,
        double sampleRateHz,
        long absoluteSampleIndex)
    {
        var phaseRadians = CreatePhaseRadians(frequencyHz, sampleRateHz, absoluteSampleIndex);
        var (sine, cosine) = MathF.SinCos(phaseRadians);
        return new DoublePhasor(cosine, sine);
    }

    internal static float CreatePhaseRadians(double frequencyHz, double sampleRateHz, long absoluteSampleIndex)
    {
        var fractionalCycles = FractionalCyclesAt(frequencyHz, sampleRateHz, absoluteSampleIndex);
        return (float)(-2 * Math.PI * fractionalCycles);
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
}