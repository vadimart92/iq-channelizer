using IqChannelizer.Abstractions;

namespace IqChannelizer.Dsp;

internal static class ScalarFir
{
    public static void Filter(ReadOnlySpan<ComplexF> input, ReadOnlySpan<float> taps, Span<ComplexF> output)
    {
        ValidateTaps(taps);
        var expectedOutputLength = Math.Max(0, input.Length - taps.Length + 1);
        if (output.Length != expectedOutputLength)
        {
            throw new ArgumentException($"Expected exactly {expectedOutputLength} FIR output samples.", nameof(output));
        }

        if (input.Overlaps(output))
        {
            throw new ArgumentException("Scalar FIR input and output must not overlap.", nameof(output));
        }

        for (var outputIndex = 0; outputIndex < output.Length; outputIndex++)
        {
            // taps[0] multiplies the newest sample at this causal output anchor.
            output[outputIndex] = FilterAt(input, taps, outputIndex + taps.Length - 1);
        }
    }

    internal static ComplexF FilterAt(ReadOnlySpan<ComplexF> input, ReadOnlySpan<float> taps, int newestInputIndex)
    {
        double real = 0;
        double imaginary = 0;
        for (var tapIndex = 0; tapIndex < taps.Length; tapIndex++)
        {
            var sample = input[newestInputIndex - tapIndex];
            real += sample.Real * taps[tapIndex];
            imaginary += sample.Imaginary * taps[tapIndex];
        }

        return new ComplexF((float)real, (float)imaginary);
    }

    internal static void ValidateTaps(ReadOnlySpan<float> taps)
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
