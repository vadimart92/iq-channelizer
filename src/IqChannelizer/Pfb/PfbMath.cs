using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;

namespace IqChannelizer.Pfb;

internal static class PfbMath
{
    public static int Mod(long value, int modulus) => FrequencyBinMath.Mod(value, modulus);

    public static void ApplyExplicitCorrection(ReadOnlySpan<ComplexF> phaseVector, long frameAnchor, Span<ComplexF> output)
    {
        ScalarDft.Backward(phaseVector, output);
        for (var bin = 0; bin < output.Length; bin++)
        {
            output[bin] *= ScalarRotator.CreatePhasor(bin, output.Length, frameAnchor);
        }
    }

    public static void ComputePhaseVector(
        ReadOnlySpan<ComplexF> input,
        long spanAbsoluteStart,
        long frameAnchor,
        ReadOnlySpan<float> prototype,
        int fftSize,
        Span<ComplexF> destination)
    {
        if (fftSize < 2 || destination.Length != fftSize || prototype.IsEmpty || prototype.Length % fftSize != 0)
        {
            throw new ArgumentException("PFB phase FIR requires K >= 2, K outputs, and a non-empty K-multiple prototype.");
        }

        var tapsPerPhase = prototype.Length / fftSize;
        for (var phase = 0; phase < fftSize; phase++)
        {
            var accumulator = new ComplexF();
            for (var tap = 0; tap < tapsPerPhase; tap++)
            {
                var prototypeIndex = phase + (tap * fftSize);
                var absoluteIndex = frameAnchor - prototypeIndex;
                var spanIndex = checked((int)(absoluteIndex - spanAbsoluteStart));
                accumulator += input[spanIndex] * prototype[prototypeIndex];
            }

            destination[phase] = accumulator;
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
