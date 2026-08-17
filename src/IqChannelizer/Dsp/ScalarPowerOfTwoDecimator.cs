using IqChannelizer.Abstractions;

namespace IqChannelizer.Dsp;

internal static class ScalarPowerOfTwoDecimator
{
    public static int GetOutputCount(int inputLength, int tapCount, int factor, int phase)
    {
        if (inputLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputLength));
        }

        if (tapCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tapCount));
        }

        if (factor <= 0 || (factor & (factor - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), "Fine decimation factor must be a positive power of two.");
        }

        if ((uint)phase >= (uint)factor)
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        var validConvolutionCount = Math.Max(0, inputLength - tapCount + 1);
        return validConvolutionCount <= phase
            ? 0
            : 1 + ((validConvolutionCount - 1 - phase) / factor);
    }

    public static void Decimate(
        ReadOnlySpan<ComplexF> input,
        ReadOnlySpan<float> taps,
        int factor,
        int phase,
        Span<ComplexF> output)
    {
        ScalarFir.ValidateTaps(taps);
        var expectedOutputCount = GetOutputCount(input.Length, taps.Length, factor, phase);
        if (output.Length != expectedOutputCount)
        {
            throw new ArgumentException($"Expected exactly {expectedOutputCount} decimator output samples.", nameof(output));
        }

        if (input.Overlaps(output))
        {
            throw new ArgumentException("Decimator input and output must not overlap.", nameof(output));
        }

        var firstNewestInputIndex = taps.Length - 1 + phase;
        for (var outputIndex = 0; outputIndex < output.Length; outputIndex++)
        {
            var newestInputIndex = firstNewestInputIndex + (outputIndex * factor);
            output[outputIndex] = ScalarFir.FilterAt(input, taps, newestInputIndex);
        }
    }
}
