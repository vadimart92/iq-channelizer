using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Pfb;

internal sealed class Avx2PfbCoefficients
{
    private const int ComplexValuesPerVector = 4;
    private const int FloatsPerVector = 8;

    public Avx2PfbCoefficients(ReadOnlySpan<float> prototype, int fftSize)
    {
        if (!Avx2.IsSupported || !Fma.IsSupported)
        {
            throw new PlatformNotSupportedException("AVX2/FMA is required by the PFB coefficient layout.");
        }

        if (fftSize <= 0 || prototype.IsEmpty || prototype.Length % fftSize != 0)
        {
            throw new ArgumentException("The PFB prototype must contain a whole number of phase rows.", nameof(prototype));
        }

        FftSize = fftSize;
        TapCountPerPhase = prototype.Length / fftSize;
        VectorBlockCount = fftSize / ComplexValuesPerVector;
        Packed = new float[checked(TapCountPerPhase * VectorBlockCount * FloatsPerVector)];
        for (var tap = 0; tap < TapCountPerPhase; tap++)
        {
            for (var block = 0; block < VectorBlockCount; block++)
            {
                var phase = block * ComplexValuesPerVector;
                var destination = (tap * VectorBlockCount + block) * FloatsPerVector;
                for (var lane = 0; lane < ComplexValuesPerVector; lane++)
                {
                    var coefficient = prototype[(tap * fftSize) + phase + (ComplexValuesPerVector - 1 - lane)];
                    Packed[destination + (lane * 2)] = coefficient;
                    Packed[destination + (lane * 2) + 1] = coefficient;
                }
            }
        }
    }

    public int FftSize { get; }
    public int TapCountPerPhase { get; }
    public int VectorBlockCount { get; }
    public float[] Packed { get; }
}

internal static class PfbPhaseFir
{
    private const int ComplexValuesPerVector = 4;
    private const int FloatsPerVector = 8;
    private static readonly Vector256<int> ReverseComplexPairs = Vector256.Create(6, 7, 4, 5, 2, 3, 0, 1);

    public static void FillBatchScalar(
        ReadOnlySpan<ComplexF> input,
        long spanAbsoluteStart,
        long firstNewSampleIndex,
        int hopSize,
        int frames,
        int fftSize,
        ReadOnlySpan<float> prototype,
        Span<ComplexF> destination)
    {
        Validate(input, hopSize, frames, fftSize, prototype, destination);
        for (var frame = 0; frame < frames; frame++)
        {
            var anchor = checked(firstNewSampleIndex + ((long)(frame + 1) * hopSize) - 1);
            var newestSpanIndex = checked((int)(anchor - spanAbsoluteStart));
            var shift = PfbMath.Mod(anchor, fftSize);
            var frameDestination = destination.Slice(frame * fftSize, fftSize);
            var firstSegmentLength = fftSize - shift;
            FillScalarSegment(input, prototype, fftSize, newestSpanIndex, shift, firstSegmentLength, frameDestination);
            FillScalarSegment(
                input,
                prototype,
                fftSize,
                newestSpanIndex,
                phaseStart: 0,
                count: shift,
                frameDestination[firstSegmentLength..]);
        }
    }

    public static void FillBatchAvx2(
        ReadOnlySpan<ComplexF> input,
        long spanAbsoluteStart,
        long firstNewSampleIndex,
        int hopSize,
        int frames,
        ReadOnlySpan<float> prototype,
        Avx2PfbCoefficients coefficients,
        Span<ComplexF> destination)
    {
        Validate(input, hopSize, frames, coefficients.FftSize, prototype, destination);
        if (!Avx2.IsSupported || !Fma.IsSupported)
        {
            throw new PlatformNotSupportedException("AVX2/FMA is required by the PFB kernel.");
        }

        if (prototype.Length / coefficients.FftSize != coefficients.TapCountPerPhase)
        {
            throw new ArgumentException("Packed AVX2 coefficients do not match the prototype.", nameof(coefficients));
        }

        var fftSize = coefficients.FftSize;
        for (var frame = 0; frame < frames; frame++)
        {
            var anchor = checked(firstNewSampleIndex + ((long)(frame + 1) * hopSize) - 1);
            var newestSpanIndex = checked((int)(anchor - spanAbsoluteStart));
            var shift = PfbMath.Mod(anchor, fftSize);
            var frameDestination = destination.Slice(frame * fftSize, fftSize);
            var firstSegmentLength = fftSize - shift;
            FillAvx2Segment(
                input,
                prototype,
                coefficients,
                newestSpanIndex,
                shift,
                firstSegmentLength,
                frameDestination);
            FillAvx2Segment(
                input,
                prototype,
                coefficients,
                newestSpanIndex,
                phaseStart: 0,
                count: shift,
                frameDestination[firstSegmentLength..]);
        }
    }

    public static void FillBatchAvx2Compact(
        ReadOnlySpan<ComplexF> input,
        long spanAbsoluteStart,
        long firstNewSampleIndex,
        int hopSize,
        int frames,
        int fftSize,
        ReadOnlySpan<float> prototype,
        Span<ComplexF> destination)
    {
        Validate(input, hopSize, frames, fftSize, prototype, destination);
        if (!Avx2.IsSupported || !Fma.IsSupported)
        {
            throw new PlatformNotSupportedException("AVX2/FMA is required by the PFB kernel.");
        }

        for (var frame = 0; frame < frames; frame++)
        {
            var anchor = checked(firstNewSampleIndex + ((long)(frame + 1) * hopSize) - 1);
            var newestSpanIndex = checked((int)(anchor - spanAbsoluteStart));
            var shift = PfbMath.Mod(anchor, fftSize);
            var frameDestination = destination.Slice(frame * fftSize, fftSize);
            var firstSegmentLength = fftSize - shift;
            FillAvx2CompactSegment(
                input,
                prototype,
                fftSize,
                newestSpanIndex,
                shift,
                firstSegmentLength,
                frameDestination);
            FillAvx2CompactSegment(
                input,
                prototype,
                fftSize,
                newestSpanIndex,
                phaseStart: 0,
                count: shift,
                frameDestination[firstSegmentLength..]);
        }
    }

    private static void FillAvx2Segment(
        ReadOnlySpan<ComplexF> input,
        ReadOnlySpan<float> prototype,
        Avx2PfbCoefficients coefficients,
        int newestSpanIndex,
        int phaseStart,
        int count,
        Span<ComplexF> destination)
    {
        var phase = phaseStart;
        var destinationIndex = 0;
        while (destinationIndex < count && (phase & (ComplexValuesPerVector - 1)) != 0)
        {
            destination[destinationIndex++] = FilterPhaseScalar(input, prototype, coefficients.FftSize, newestSpanIndex, phase++);
        }

        var vectorEnd = phase + ((count - destinationIndex) & ~(ComplexValuesPerVector - 1));
        var inputFloats = MemoryMarshal.Cast<ComplexF, float>(input);
        var destinationFloats = MemoryMarshal.Cast<ComplexF, float>(destination);
        ref var inputReference = ref MemoryMarshal.GetReference(inputFloats);
        ref var destinationReference = ref MemoryMarshal.GetReference(destinationFloats);
        ref var coefficientReference = ref MemoryMarshal.GetArrayDataReference(coefficients.Packed);
        for (; phase < vectorEnd; phase += ComplexValuesPerVector, destinationIndex += ComplexValuesPerVector)
        {
            var accumulator = Vector256<float>.Zero;
            var vectorBlock = phase / ComplexValuesPerVector;
            for (var tap = 0; tap < coefficients.TapCountPerPhase; tap++)
            {
                var firstSampleIndex = newestSpanIndex - phase - (ComplexValuesPerVector - 1) - (tap * coefficients.FftSize);
                var samples = Vector256.LoadUnsafe(ref inputReference, (nuint)(firstSampleIndex * 2));
                var coefficientOffset = (tap * coefficients.VectorBlockCount + vectorBlock) * FloatsPerVector;
                var packedCoefficients = Vector256.LoadUnsafe(ref coefficientReference, (nuint)coefficientOffset);
                accumulator = Fma.MultiplyAdd(samples, packedCoefficients, accumulator);
            }

            var ordered = Avx2.PermuteVar8x32(accumulator.AsInt32(), ReverseComplexPairs).AsSingle();
            ordered.StoreUnsafe(ref destinationReference, (nuint)(destinationIndex * 2));
        }

        while (destinationIndex < count)
        {
            destination[destinationIndex++] = FilterPhaseScalar(input, prototype, coefficients.FftSize, newestSpanIndex, phase++);
        }
    }

    private static void FillAvx2CompactSegment(
        ReadOnlySpan<ComplexF> input,
        ReadOnlySpan<float> prototype,
        int fftSize,
        int newestSpanIndex,
        int phaseStart,
        int count,
        Span<ComplexF> destination)
    {
        var phase = phaseStart;
        var destinationIndex = 0;
        while (destinationIndex < count && (phase & (ComplexValuesPerVector - 1)) != 0)
        {
            destination[destinationIndex++] = FilterPhaseScalar(input, prototype, fftSize, newestSpanIndex, phase++);
        }

        var vectorEnd = phase + ((count - destinationIndex) & ~(ComplexValuesPerVector - 1));
        var inputFloats = MemoryMarshal.Cast<ComplexF, float>(input);
        var destinationFloats = MemoryMarshal.Cast<ComplexF, float>(destination);
        ref var inputReference = ref MemoryMarshal.GetReference(inputFloats);
        ref var destinationReference = ref MemoryMarshal.GetReference(destinationFloats);
        var tapCountPerPhase = prototype.Length / fftSize;
        for (; phase < vectorEnd; phase += ComplexValuesPerVector, destinationIndex += ComplexValuesPerVector)
        {
            var accumulator = Vector256<float>.Zero;
            for (var tap = 0; tap < tapCountPerPhase; tap++)
            {
                var firstSampleIndex = newestSpanIndex - phase - (ComplexValuesPerVector - 1) - (tap * fftSize);
                var samples = Vector256.LoadUnsafe(ref inputReference, (nuint)(firstSampleIndex * 2));
                var coefficientOffset = (tap * fftSize) + phase;
                var packedCoefficients = Vector256.Create(
                    prototype[coefficientOffset + 3], prototype[coefficientOffset + 3],
                    prototype[coefficientOffset + 2], prototype[coefficientOffset + 2],
                    prototype[coefficientOffset + 1], prototype[coefficientOffset + 1],
                    prototype[coefficientOffset], prototype[coefficientOffset]);
                accumulator = Fma.MultiplyAdd(samples, packedCoefficients, accumulator);
            }

            var ordered = Avx2.PermuteVar8x32(accumulator.AsInt32(), ReverseComplexPairs).AsSingle();
            ordered.StoreUnsafe(ref destinationReference, (nuint)(destinationIndex * 2));
        }

        while (destinationIndex < count)
        {
            destination[destinationIndex++] = FilterPhaseScalar(input, prototype, fftSize, newestSpanIndex, phase++);
        }
    }

    private static void FillScalarSegment(
        ReadOnlySpan<ComplexF> input,
        ReadOnlySpan<float> prototype,
        int fftSize,
        int newestSpanIndex,
        int phaseStart,
        int count,
        Span<ComplexF> destination)
    {
        for (var index = 0; index < count; index++)
        {
            destination[index] = FilterPhaseScalar(input, prototype, fftSize, newestSpanIndex, phaseStart + index);
        }
    }

    private static ComplexF FilterPhaseScalar(
        ReadOnlySpan<ComplexF> input,
        ReadOnlySpan<float> prototype,
        int fftSize,
        int newestSpanIndex,
        int phase)
    {
        var accumulator = new ComplexF();
        for (var tap = phase; tap < prototype.Length; tap += fftSize)
        {
            accumulator += input[newestSpanIndex - tap] * prototype[tap];
        }

        return accumulator;
    }

    private static void Validate(
        ReadOnlySpan<ComplexF> input,
        int hopSize,
        int frames,
        int fftSize,
        ReadOnlySpan<float> prototype,
        Span<ComplexF> destination)
    {
        if (hopSize <= 0 || frames <= 0 || fftSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frames), "PFB dimensions must be positive.");
        }

        if (prototype.IsEmpty || prototype.Length % fftSize != 0)
        {
            throw new ArgumentException("Prototype length must be a positive multiple of the FFT size.", nameof(prototype));
        }

        if (destination.Length != checked(frames * fftSize))
        {
            throw new ArgumentException("PFB destination must contain one FFT vector per frame.", nameof(destination));
        }

        if (input.IsEmpty || input.Overlaps(destination))
        {
            throw new ArgumentException("PFB input must be non-empty and must not overlap its FFT destination.", nameof(input));
        }
    }
}
