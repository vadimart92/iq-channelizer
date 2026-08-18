using System.Numerics;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Reference;

internal static class PfbDirectReference
{
    public static Complex[] Evaluate(
        ReadOnlySpan<ComplexF> input,
        long spanAbsoluteStart,
        long frameAnchor,
        ReadOnlySpan<float> prototype,
        int fftSize)
    {
        if (fftSize < 2 || prototype.IsEmpty || prototype.Length % fftSize != 0)
        {
            throw new ArgumentException("Reference PFB requires K >= 2 and a non-empty K-multiple prototype.");
        }

        var output = new Complex[fftSize];
        for (var bin = 0; bin < fftSize; bin++)
        {
            var sum = Complex.Zero;
            for (var tapIndex = 0; tapIndex < prototype.Length; tapIndex++)
            {
                var absoluteIndex = frameAnchor - tapIndex;
                var spanIndex = checked((int)(absoluteIndex - spanAbsoluteStart));
                var sample = input[spanIndex];
                var phase = -2 * Math.PI * bin * absoluteIndex / fftSize;
                sum += new Complex(sample.Real, sample.Imaginary) *
                       Complex.FromPolarCoordinates(prototype[tapIndex], phase);
            }

            output[bin] = sum;
        }

        return output;
    }
}
