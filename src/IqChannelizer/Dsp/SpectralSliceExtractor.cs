using IqChannelizer.Abstractions;

namespace IqChannelizer.Dsp;

internal static class SpectralSliceExtractor
{
    public static void Extract(
        ReadOnlySpan<ComplexF> fullSpectrum,
        int centerBin,
        ReadOnlySpan<float> window,
        ComplexF blockPhase,
        Span<ComplexF> destination)
    {
        if (fullSpectrum.IsEmpty)
        {
            throw new ArgumentException("Full spectrum must not be empty.", nameof(fullSpectrum));
        }

        if (destination.IsEmpty || destination.Length > fullSpectrum.Length)
        {
            throw new ArgumentException("Spectral slice length must be in [1, full spectrum length].", nameof(destination));
        }

        if (window.Length != destination.Length)
        {
            throw new ArgumentException("Window and destination lengths must match.", nameof(window));
        }

        if (!float.IsFinite(blockPhase.Real) || !float.IsFinite(blockPhase.Imaginary))
        {
            throw new ArgumentException("Block phase must be finite.", nameof(blockPhase));
        }

        foreach (var coefficient in window)
        {
            if (!float.IsFinite(coefficient))
            {
                throw new ArgumentException("Window coefficients must be finite.", nameof(window));
            }
        }

        var normalizedCenter = Mod(centerBin, fullSpectrum.Length);
        // Short-IFFT order is coarse/DC first, positive offsets through Nyquist, then negative offsets.
        var positiveLength = (destination.Length / 2) + 1;
        CopyCircularSegment(
            fullSpectrum,
            normalizedCenter,
            window,
            destination,
            destinationOffset: 0,
            positiveLength,
            blockPhase);

        var negativeLength = destination.Length - positiveLength;
        if (negativeLength > 0)
        {
            CopyCircularSegment(
                fullSpectrum,
                normalizedCenter - negativeLength,
                window,
                destination,
                positiveLength,
                negativeLength,
                blockPhase);
        }
    }

    private static void CopyCircularSegment(
        ReadOnlySpan<ComplexF> source,
        int sourceStart,
        ReadOnlySpan<float> window,
        Span<ComplexF> destination,
        int destinationOffset,
        int count,
        ComplexF blockPhase)
    {
        var normalizedStart = Mod(sourceStart, source.Length);
        var firstCount = Math.Min(count, source.Length - normalizedStart);
        CopyContiguous(source[normalizedStart..], window, destination, destinationOffset, firstCount, blockPhase);
        var remaining = count - firstCount;
        if (remaining > 0)
        {
            CopyContiguous(source, window, destination, destinationOffset + firstCount, remaining, blockPhase);
        }
    }

    private static void CopyContiguous(
        ReadOnlySpan<ComplexF> source,
        ReadOnlySpan<float> window,
        Span<ComplexF> destination,
        int destinationOffset,
        int count,
        ComplexF blockPhase)
    {
        for (var index = 0; index < count; index++)
        {
            destination[destinationOffset + index] =
                (source[index] * window[destinationOffset + index]) * blockPhase;
        }
    }

    private static int Mod(int value, int modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }
}
