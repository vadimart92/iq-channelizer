using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;

namespace IqChannelizer.Fdc;

internal static class PartitionedSpectralAccumulator
{
    public static void AccumulateUnchecked(
        ReadOnlySpan<ComplexF> fullSpectrum,
        int centerBin,
        ReadOnlySpan<ComplexF> window,
        Span<ComplexF> destination,
        SimdPreference simdBackend)
    {
        var normalizedCenter = FrequencyBinMath.Mod(centerBin, fullSpectrum.Length);
        var positiveLength = (destination.Length / 2) + 1;
        AccumulateCircular(
            fullSpectrum,
            normalizedCenter,
            window,
            destination,
            destinationOffset: 0,
            positiveLength,
            simdBackend);

        var negativeLength = destination.Length - positiveLength;
        if (negativeLength > 0)
        {
            AccumulateCircular(
                fullSpectrum,
                normalizedCenter - negativeLength,
                window,
                destination,
                positiveLength,
                negativeLength,
                simdBackend);
        }
    }

    public static void ApplyPhaseUnchecked(
        Span<ComplexF> values,
        ComplexF phase,
        SimdPreference simdBackend)
    {
        if (simdBackend == SimdPreference.Avx512)
        {
            Avx512ComplexKernels.MultiplyComplexByScalar(values, phase, values);
        }
        else if (simdBackend == SimdPreference.Avx2)
        {
            Avx2ComplexKernels.MultiplyComplexByScalar(values, phase, values);
        }
        else
        {
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = values[index] * phase;
            }
        }
    }

    private static void AccumulateCircular(
        ReadOnlySpan<ComplexF> source,
        int sourceStart,
        ReadOnlySpan<ComplexF> window,
        Span<ComplexF> destination,
        int destinationOffset,
        int count,
        SimdPreference simdBackend)
    {
        var normalizedStart = FrequencyBinMath.Mod(sourceStart, source.Length);
        var firstCount = Math.Min(count, source.Length - normalizedStart);
        AccumulateContiguous(
            source.Slice(normalizedStart, firstCount),
            window.Slice(destinationOffset, firstCount),
            destination.Slice(destinationOffset, firstCount),
            simdBackend);
        var remaining = count - firstCount;
        if (remaining > 0)
        {
            AccumulateContiguous(
                source[..remaining],
                window.Slice(destinationOffset + firstCount, remaining),
                destination.Slice(destinationOffset + firstCount, remaining),
                simdBackend);
        }
    }

    private static void AccumulateContiguous(
        ReadOnlySpan<ComplexF> source,
        ReadOnlySpan<ComplexF> window,
        Span<ComplexF> destination,
        SimdPreference simdBackend)
    {
        if (simdBackend == SimdPreference.Avx512)
        {
            AccumulateAvx512(source, window, destination);
            return;
        }

        if (simdBackend == SimdPreference.Avx2)
        {
            AccumulateAvx2(source, window, destination);
            return;
        }

        for (var index = 0; index < destination.Length; index++)
        {
            destination[index] = destination[index] + (source[index] * window[index]);
        }
    }

    private static void AccumulateAvx2(
        ReadOnlySpan<ComplexF> source,
        ReadOnlySpan<ComplexF> window,
        Span<ComplexF> destination)
    {
        const int complexValuesPerVector = 4;
        var sourceFloats = MemoryMarshal.Cast<ComplexF, float>(source);
        var windowFloats = MemoryMarshal.Cast<ComplexF, float>(window);
        var destinationFloats = MemoryMarshal.Cast<ComplexF, float>(destination);
        ref var sourceReference = ref MemoryMarshal.GetReference(sourceFloats);
        ref var windowReference = ref MemoryMarshal.GetReference(windowFloats);
        ref var destinationReference = ref MemoryMarshal.GetReference(destinationFloats);
        var index = 0;
        for (; index <= destination.Length - complexValuesPerVector; index += complexValuesPerVector)
        {
            var floatIndex = index * 2;
            var product = Avx2ComplexKernels.MultiplyComplex(
                Vector256.LoadUnsafe(ref sourceReference, (nuint)floatIndex),
                Vector256.LoadUnsafe(ref windowReference, (nuint)floatIndex));
            var accumulated = Avx.Add(
                Vector256.LoadUnsafe(ref destinationReference, (nuint)floatIndex),
                product);
            accumulated.StoreUnsafe(ref destinationReference, (nuint)floatIndex);
        }

        for (; index < destination.Length; index++)
        {
            destination[index] = destination[index] + (source[index] * window[index]);
        }
    }

    private static void AccumulateAvx512(
        ReadOnlySpan<ComplexF> source,
        ReadOnlySpan<ComplexF> window,
        Span<ComplexF> destination)
    {
        const int complexValuesPerVector = 8;
        var sourceFloats = MemoryMarshal.Cast<ComplexF, float>(source);
        var windowFloats = MemoryMarshal.Cast<ComplexF, float>(window);
        var destinationFloats = MemoryMarshal.Cast<ComplexF, float>(destination);
        ref var sourceReference = ref MemoryMarshal.GetReference(sourceFloats);
        ref var windowReference = ref MemoryMarshal.GetReference(windowFloats);
        ref var destinationReference = ref MemoryMarshal.GetReference(destinationFloats);
        var index = 0;
        for (; index <= destination.Length - complexValuesPerVector; index += complexValuesPerVector)
        {
            var floatIndex = index * 2;
            var product = Avx512ComplexKernels.MultiplyComplex(
                Vector512.LoadUnsafe(ref sourceReference, (nuint)floatIndex),
                Vector512.LoadUnsafe(ref windowReference, (nuint)floatIndex));
            var accumulated = Avx512F.Add(
                Vector512.LoadUnsafe(ref destinationReference, (nuint)floatIndex),
                product);
            accumulated.StoreUnsafe(ref destinationReference, (nuint)floatIndex);
        }

        for (; index < destination.Length; index++)
        {
            destination[index] = destination[index] + (source[index] * window[index]);
        }
    }
}
