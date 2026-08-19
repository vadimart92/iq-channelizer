using IqChannelizer.Abstractions;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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

    public static void ExtractAvx2(
        ReadOnlySpan<ComplexF> fullSpectrum,
        int centerBin,
        ReadOnlySpan<ComplexF> window,
        ComplexF blockPhase,
        Span<ComplexF> destination)
    {
        Validate(fullSpectrum, window.Length, blockPhase, destination);
        ValidateComplexWindow(window);
        if (!Avx2.IsSupported || !Fma.IsSupported)
        {
            throw new PlatformNotSupportedException("AVX2/FMA is required by the spectral extraction kernel.");
        }

        ExtractAvx2Unchecked(fullSpectrum, centerBin, window, blockPhase, destination);
    }

    internal static void ExtractAvx2Unchecked(
        ReadOnlySpan<ComplexF> fullSpectrum,
        int centerBin,
        ReadOnlySpan<ComplexF> window,
        ComplexF blockPhase,
        Span<ComplexF> destination)
    {
        var normalizedCenter = Mod(centerBin, fullSpectrum.Length);
        var positiveLength = (destination.Length / 2) + 1;
        CopyCircularSegmentAvx2(fullSpectrum, normalizedCenter, window, destination, 0, positiveLength, blockPhase);
        var negativeLength = destination.Length - positiveLength;
        if (negativeLength > 0)
        {
            CopyCircularSegmentAvx2(
                fullSpectrum,
                normalizedCenter - negativeLength,
                window,
                destination,
                positiveLength,
                negativeLength,
                blockPhase);
        }
    }

    public static void ExtractAvx512(
        ReadOnlySpan<ComplexF> fullSpectrum,
        int centerBin,
        ReadOnlySpan<ComplexF> window,
        ComplexF blockPhase,
        Span<ComplexF> destination)
    {
        Validate(fullSpectrum, window.Length, blockPhase, destination);
        ValidateComplexWindow(window);
        if (!Avx512F.IsSupported)
        {
            throw new PlatformNotSupportedException("AVX-512F is required by the spectral extraction kernel.");
        }

        ExtractAvx512Unchecked(fullSpectrum, centerBin, window, blockPhase, destination);
    }

    internal static void ExtractAvx512Unchecked(
        ReadOnlySpan<ComplexF> fullSpectrum,
        int centerBin,
        ReadOnlySpan<ComplexF> window,
        ComplexF blockPhase,
        Span<ComplexF> destination)
    {
        var normalizedCenter = Mod(centerBin, fullSpectrum.Length);
        var positiveLength = (destination.Length / 2) + 1;
        CopyCircularSegmentAvx512(fullSpectrum, normalizedCenter, window, destination, 0, positiveLength, blockPhase);
        var negativeLength = destination.Length - positiveLength;
        if (negativeLength > 0)
        {
            CopyCircularSegmentAvx512(
                fullSpectrum,
                normalizedCenter - negativeLength,
                window,
                destination,
                positiveLength,
                negativeLength,
                blockPhase);
        }
    }

    public static void Extract(
        ReadOnlySpan<ComplexF> fullSpectrum,
        int centerBin,
        ReadOnlySpan<ComplexF> window,
        ComplexF blockPhase,
        Span<ComplexF> destination)
    {
        Validate(fullSpectrum, window.Length, blockPhase, destination);
        ValidateComplexWindow(window);
        ExtractUnchecked(fullSpectrum, centerBin, window, blockPhase, destination);
    }

    internal static void ExtractUnchecked(
        ReadOnlySpan<ComplexF> fullSpectrum,
        int centerBin,
        ReadOnlySpan<ComplexF> window,
        ComplexF blockPhase,
        Span<ComplexF> destination)
    {
        var normalizedCenter = Mod(centerBin, fullSpectrum.Length);
        var positiveLength = (destination.Length / 2) + 1;
        CopyCircularSegment(fullSpectrum, normalizedCenter, window, destination, 0, positiveLength, blockPhase);
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

    private static void ValidateComplexWindow(ReadOnlySpan<ComplexF> window)
    {
        foreach (var coefficient in window)
        {
            if (!float.IsFinite(coefficient.Real) || !float.IsFinite(coefficient.Imaginary))
            {
                throw new ArgumentException("Window coefficients must be finite.", nameof(window));
            }
        }
    }

    private static void Validate(
        ReadOnlySpan<ComplexF> fullSpectrum,
        int windowLength,
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

        if (windowLength != destination.Length)
        {
            throw new ArgumentException("Window and destination lengths must match.", "window");
        }

        if (!float.IsFinite(blockPhase.Real) || !float.IsFinite(blockPhase.Imaginary))
        {
            throw new ArgumentException("Block phase must be finite.", nameof(blockPhase));
        }
    }

    private static void CopyCircularSegment(
        ReadOnlySpan<ComplexF> source,
        int sourceStart,
        ReadOnlySpan<ComplexF> window,
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

    private static void CopyCircularSegmentAvx2(
        ReadOnlySpan<ComplexF> source,
        int sourceStart,
        ReadOnlySpan<ComplexF> window,
        Span<ComplexF> destination,
        int destinationOffset,
        int count,
        ComplexF blockPhase)
    {
        var normalizedStart = Mod(sourceStart, source.Length);
        var firstCount = Math.Min(count, source.Length - normalizedStart);
        CopyContiguousAvx2(source[normalizedStart..], window, destination, destinationOffset, firstCount, blockPhase);
        var remaining = count - firstCount;
        if (remaining > 0)
        {
            CopyContiguousAvx2(source, window, destination, destinationOffset + firstCount, remaining, blockPhase);
        }
    }

    private static void CopyContiguousAvx2(
        ReadOnlySpan<ComplexF> source,
        ReadOnlySpan<ComplexF> window,
        Span<ComplexF> destination,
        int destinationOffset,
        int count,
        ComplexF blockPhase)
    {
        const int complexValuesPerVector = 4;
        var sourceFloats = MemoryMarshal.Cast<ComplexF, float>(source);
        var windowFloats = MemoryMarshal.Cast<ComplexF, float>(window[destinationOffset..]);
        var destinationFloats = MemoryMarshal.Cast<ComplexF, float>(destination[destinationOffset..]);
        var phaseVector = Vector256.Create(
            blockPhase.Real, blockPhase.Imaginary,
            blockPhase.Real, blockPhase.Imaginary,
            blockPhase.Real, blockPhase.Imaginary,
            blockPhase.Real, blockPhase.Imaginary);
        ref var sourceReference = ref MemoryMarshal.GetReference(sourceFloats);
        ref var windowReference = ref MemoryMarshal.GetReference(windowFloats);
        ref var destinationReference = ref MemoryMarshal.GetReference(destinationFloats);
        var index = 0;
        for (; index <= count - complexValuesPerVector; index += complexValuesPerVector)
        {
            var floatIndex = index * 2;
            var sourceValues = Vector256.LoadUnsafe(ref sourceReference, (nuint)floatIndex);
            var windowValues = Vector256.LoadUnsafe(ref windowReference, (nuint)floatIndex);
            var filtered = Avx2ComplexKernels.MultiplyComplex(sourceValues, windowValues);
            var phased = Avx2ComplexKernels.MultiplyComplex(filtered, phaseVector);
            phased.StoreUnsafe(ref destinationReference, (nuint)floatIndex);
        }

        for (; index < count; index++)
        {
            destination[destinationOffset + index] =
                (source[index] * window[destinationOffset + index]) * blockPhase;
        }
    }

    private static void CopyCircularSegmentAvx512(
        ReadOnlySpan<ComplexF> source,
        int sourceStart,
        ReadOnlySpan<ComplexF> window,
        Span<ComplexF> destination,
        int destinationOffset,
        int count,
        ComplexF blockPhase)
    {
        var normalizedStart = Mod(sourceStart, source.Length);
        var firstCount = Math.Min(count, source.Length - normalizedStart);
        CopyContiguousAvx512(source[normalizedStart..], window, destination, destinationOffset, firstCount, blockPhase);
        var remaining = count - firstCount;
        if (remaining > 0)
        {
            CopyContiguousAvx512(source, window, destination, destinationOffset + firstCount, remaining, blockPhase);
        }
    }

    private static void CopyContiguousAvx512(
        ReadOnlySpan<ComplexF> source,
        ReadOnlySpan<ComplexF> window,
        Span<ComplexF> destination,
        int destinationOffset,
        int count,
        ComplexF blockPhase)
    {
        const int complexValuesPerVector = 8;
        var sourceFloats = MemoryMarshal.Cast<ComplexF, float>(source);
        var windowFloats = MemoryMarshal.Cast<ComplexF, float>(window[destinationOffset..]);
        var destinationFloats = MemoryMarshal.Cast<ComplexF, float>(destination[destinationOffset..]);
        var phaseVector = Vector512.Create(
            blockPhase.Real, blockPhase.Imaginary, blockPhase.Real, blockPhase.Imaginary,
            blockPhase.Real, blockPhase.Imaginary, blockPhase.Real, blockPhase.Imaginary,
            blockPhase.Real, blockPhase.Imaginary, blockPhase.Real, blockPhase.Imaginary,
            blockPhase.Real, blockPhase.Imaginary, blockPhase.Real, blockPhase.Imaginary);
        ref var sourceReference = ref MemoryMarshal.GetReference(sourceFloats);
        ref var windowReference = ref MemoryMarshal.GetReference(windowFloats);
        ref var destinationReference = ref MemoryMarshal.GetReference(destinationFloats);
        var index = 0;
        for (; index <= count - complexValuesPerVector; index += complexValuesPerVector)
        {
            var floatIndex = index * 2;
            var sourceValues = Vector512.LoadUnsafe(ref sourceReference, (nuint)floatIndex);
            var windowValues = Vector512.LoadUnsafe(ref windowReference, (nuint)floatIndex);
            var filtered = Avx512ComplexKernels.MultiplyComplex(sourceValues, windowValues);
            var phased = Avx512ComplexKernels.MultiplyComplex(filtered, phaseVector);
            phased.StoreUnsafe(ref destinationReference, (nuint)floatIndex);
        }

        for (; index < count; index++)
        {
            destination[destinationOffset + index] =
                (source[index] * window[destinationOffset + index]) * blockPhase;
        }
    }

    private static void CopyContiguous(
        ReadOnlySpan<ComplexF> source,
        ReadOnlySpan<ComplexF> window,
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
