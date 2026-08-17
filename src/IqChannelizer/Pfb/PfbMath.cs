using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;

namespace IqChannelizer.Pfb;

internal static class PfbMath
{
    public static int Mod(long value, int modulus)
    {
        var result = (int)(value % modulus);
        return result < 0 ? result + modulus : result;
    }

    public static void ApplyExplicitCorrection(ReadOnlySpan<ComplexF> phaseVector, long frameAnchor, Span<ComplexF> output)
    {
        ScalarDft.Backward(phaseVector, output);
        for (var bin = 0; bin < output.Length; bin++)
        {
            output[bin] *= ComplexF.FromPolar(-2 * Math.PI * bin * frameAnchor / output.Length);
        }
    }

    public static void TransformWithCircularShift(ReadOnlySpan<ComplexF> phaseVector, long frameAnchor, Span<ComplexF> shifted, Span<ComplexF> output)
    {
        var shift = Mod(frameAnchor, phaseVector.Length);
        for (var phase = 0; phase < phaseVector.Length; phase++)
        {
            shifted[phase] = phaseVector[(phase + shift) % phaseVector.Length];
        }

        ScalarDft.Backward(shifted, output);
    }
}
