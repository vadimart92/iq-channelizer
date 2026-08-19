using System.Runtime.CompilerServices;
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

internal sealed class Avx512PfbCoefficients
{
    private const int ComplexValuesPerVector = 8;
    private const int FloatsPerVector = 16;

    public Avx512PfbCoefficients(ReadOnlySpan<float> prototype, int fftSize)
    {
        if (!Avx512F.IsSupported)
        {
            throw new PlatformNotSupportedException("AVX-512F is required by the PFB coefficient layout.");
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
    private const int ComplexValuesPerVector512 = 8;
    private const int FloatsPerVector512 = 16;
    private static readonly Vector256<int> ReverseComplexPairs = Vector256.Create(6, 7, 4, 5, 2, 3, 0, 1);
    private static readonly Vector512<int> ReverseComplexPairs512 = Vector512.Create(
        14, 15, 12, 13, 10, 11, 8, 9, 6, 7, 4, 5, 2, 3, 0, 1);

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
        ValidateAvx2(input, hopSize, frames, prototype, coefficients, destination);
        switch (coefficients.TapCountPerPhase)
        {
            case 4:
                FillBatchAvx2Specialized<Avx2Tap4Kernel>(input, spanAbsoluteStart, firstNewSampleIndex, hopSize, frames, prototype, coefficients, destination);
                break;
            case 8:
                FillBatchAvx2Specialized<Avx2Tap8Kernel>(input, spanAbsoluteStart, firstNewSampleIndex, hopSize, frames, prototype, coefficients, destination);
                break;
            case 12:
                FillBatchAvx2Specialized<Avx2Tap12Kernel>(input, spanAbsoluteStart, firstNewSampleIndex, hopSize, frames, prototype, coefficients, destination);
                break;
            case 16:
                FillBatchAvx2Specialized<Avx2Tap16Kernel>(input, spanAbsoluteStart, firstNewSampleIndex, hopSize, frames, prototype, coefficients, destination);
                break;
            default:
                FillBatchAvx2GenericCore(input, spanAbsoluteStart, firstNewSampleIndex, hopSize, frames, prototype, coefficients, destination);
                break;
        }
    }

    public static void FillBatchAvx2Generic(
        ReadOnlySpan<ComplexF> input,
        long spanAbsoluteStart,
        long firstNewSampleIndex,
        int hopSize,
        int frames,
        ReadOnlySpan<float> prototype,
        Avx2PfbCoefficients coefficients,
        Span<ComplexF> destination)
    {
        ValidateAvx2(input, hopSize, frames, prototype, coefficients, destination);
        FillBatchAvx2GenericCore(
            input,
            spanAbsoluteStart,
            firstNewSampleIndex,
            hopSize,
            frames,
            prototype,
            coefficients,
            destination);
    }

    public static void FillBatchAvx512(
        ReadOnlySpan<ComplexF> input,
        long spanAbsoluteStart,
        long firstNewSampleIndex,
        int hopSize,
        int frames,
        ReadOnlySpan<float> prototype,
        Avx512PfbCoefficients coefficients,
        Span<ComplexF> destination)
    {
        ValidateAvx512(input, hopSize, frames, prototype, coefficients, destination);
        switch (coefficients.TapCountPerPhase)
        {
            case 4:
                FillBatchAvx512Specialized<Avx512Tap4Kernel>(input, spanAbsoluteStart, firstNewSampleIndex, hopSize, frames, prototype, coefficients, destination);
                break;
            case 8:
                FillBatchAvx512Specialized<Avx512Tap8Kernel>(input, spanAbsoluteStart, firstNewSampleIndex, hopSize, frames, prototype, coefficients, destination);
                break;
            case 12:
                FillBatchAvx512Specialized<Avx512Tap12Kernel>(input, spanAbsoluteStart, firstNewSampleIndex, hopSize, frames, prototype, coefficients, destination);
                break;
            case 16:
                FillBatchAvx512Specialized<Avx512Tap16Kernel>(input, spanAbsoluteStart, firstNewSampleIndex, hopSize, frames, prototype, coefficients, destination);
                break;
            default:
                FillBatchAvx512GenericCore(input, spanAbsoluteStart, firstNewSampleIndex, hopSize, frames, prototype, coefficients, destination);
                break;
        }
    }

    public static void FillBatchAvx512Generic(
        ReadOnlySpan<ComplexF> input,
        long spanAbsoluteStart,
        long firstNewSampleIndex,
        int hopSize,
        int frames,
        ReadOnlySpan<float> prototype,
        Avx512PfbCoefficients coefficients,
        Span<ComplexF> destination)
    {
        ValidateAvx512(input, hopSize, frames, prototype, coefficients, destination);
        FillBatchAvx512GenericCore(
            input,
            spanAbsoluteStart,
            firstNewSampleIndex,
            hopSize,
            frames,
            prototype,
            coefficients,
            destination);
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

    private static void FillBatchAvx2GenericCore(
        ReadOnlySpan<ComplexF> input,
        long spanAbsoluteStart,
        long firstNewSampleIndex,
        int hopSize,
        int frames,
        ReadOnlySpan<float> prototype,
        Avx2PfbCoefficients coefficients,
        Span<ComplexF> destination)
    {
        var fftSize = coefficients.FftSize;
        for (var frame = 0; frame < frames; frame++)
        {
            var anchor = checked(firstNewSampleIndex + ((long)(frame + 1) * hopSize) - 1);
            var newestSpanIndex = checked((int)(anchor - spanAbsoluteStart));
            var shift = PfbMath.Mod(anchor, fftSize);
            var frameDestination = destination.Slice(frame * fftSize, fftSize);
            var firstSegmentLength = fftSize - shift;
            FillAvx2SegmentGeneric(
                input,
                prototype,
                coefficients,
                newestSpanIndex,
                shift,
                firstSegmentLength,
                frameDestination);
            FillAvx2SegmentGeneric(
                input,
                prototype,
                coefficients,
                newestSpanIndex,
                phaseStart: 0,
                count: shift,
                frameDestination[firstSegmentLength..]);
        }
    }

    private static void FillBatchAvx2Specialized<TTapKernel>(
        ReadOnlySpan<ComplexF> input,
        long spanAbsoluteStart,
        long firstNewSampleIndex,
        int hopSize,
        int frames,
        ReadOnlySpan<float> prototype,
        Avx2PfbCoefficients coefficients,
        Span<ComplexF> destination)
        where TTapKernel : struct, IAvx2TapKernel
    {
        var fftSize = coefficients.FftSize;
        for (var frame = 0; frame < frames; frame++)
        {
            var anchor = checked(firstNewSampleIndex + ((long)(frame + 1) * hopSize) - 1);
            var newestSpanIndex = checked((int)(anchor - spanAbsoluteStart));
            var shift = PfbMath.Mod(anchor, fftSize);
            var frameDestination = destination.Slice(frame * fftSize, fftSize);
            var firstSegmentLength = fftSize - shift;
            FillAvx2SegmentSpecialized<TTapKernel>(
                input,
                prototype,
                coefficients,
                newestSpanIndex,
                shift,
                firstSegmentLength,
                frameDestination);
            FillAvx2SegmentSpecialized<TTapKernel>(
                input,
                prototype,
                coefficients,
                newestSpanIndex,
                phaseStart: 0,
                count: shift,
                frameDestination[firstSegmentLength..]);
        }
    }

    private static void FillBatchAvx512GenericCore(
        ReadOnlySpan<ComplexF> input,
        long spanAbsoluteStart,
        long firstNewSampleIndex,
        int hopSize,
        int frames,
        ReadOnlySpan<float> prototype,
        Avx512PfbCoefficients coefficients,
        Span<ComplexF> destination)
    {
        var fftSize = coefficients.FftSize;
        for (var frame = 0; frame < frames; frame++)
        {
            var anchor = checked(firstNewSampleIndex + ((long)(frame + 1) * hopSize) - 1);
            var newestSpanIndex = checked((int)(anchor - spanAbsoluteStart));
            var shift = PfbMath.Mod(anchor, fftSize);
            var frameDestination = destination.Slice(frame * fftSize, fftSize);
            var firstSegmentLength = fftSize - shift;
            FillAvx512SegmentGeneric(
                input,
                prototype,
                coefficients,
                newestSpanIndex,
                shift,
                firstSegmentLength,
                frameDestination);
            FillAvx512SegmentGeneric(
                input,
                prototype,
                coefficients,
                newestSpanIndex,
                phaseStart: 0,
                count: shift,
                frameDestination[firstSegmentLength..]);
        }
    }

    private static void FillBatchAvx512Specialized<TTapKernel>(
        ReadOnlySpan<ComplexF> input,
        long spanAbsoluteStart,
        long firstNewSampleIndex,
        int hopSize,
        int frames,
        ReadOnlySpan<float> prototype,
        Avx512PfbCoefficients coefficients,
        Span<ComplexF> destination)
        where TTapKernel : struct, IAvx512TapKernel
    {
        var fftSize = coefficients.FftSize;
        for (var frame = 0; frame < frames; frame++)
        {
            var anchor = checked(firstNewSampleIndex + ((long)(frame + 1) * hopSize) - 1);
            var newestSpanIndex = checked((int)(anchor - spanAbsoluteStart));
            var shift = PfbMath.Mod(anchor, fftSize);
            var frameDestination = destination.Slice(frame * fftSize, fftSize);
            var firstSegmentLength = fftSize - shift;
            FillAvx512SegmentSpecialized<TTapKernel>(
                input,
                prototype,
                coefficients,
                newestSpanIndex,
                shift,
                firstSegmentLength,
                frameDestination);
            FillAvx512SegmentSpecialized<TTapKernel>(
                input,
                prototype,
                coefficients,
                newestSpanIndex,
                phaseStart: 0,
                count: shift,
                frameDestination[firstSegmentLength..]);
        }
    }

    private static void FillAvx2SegmentGeneric(
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

    private static void FillAvx512SegmentGeneric(
        ReadOnlySpan<ComplexF> input,
        ReadOnlySpan<float> prototype,
        Avx512PfbCoefficients coefficients,
        int newestSpanIndex,
        int phaseStart,
        int count,
        Span<ComplexF> destination)
    {
        const int complexValuesPerVector = 8;
        const int floatsPerVector = 16;
        var phase = phaseStart;
        var destinationIndex = 0;
        while (destinationIndex < count && (phase & (complexValuesPerVector - 1)) != 0)
        {
            destination[destinationIndex++] =
                FilterPhaseScalar(input, prototype, coefficients.FftSize, newestSpanIndex, phase++);
        }

        var vectorEnd = phase + ((count - destinationIndex) & ~(complexValuesPerVector - 1));
        var inputFloats = MemoryMarshal.Cast<ComplexF, float>(input);
        var destinationFloats = MemoryMarshal.Cast<ComplexF, float>(destination);
        ref var inputReference = ref MemoryMarshal.GetReference(inputFloats);
        ref var destinationReference = ref MemoryMarshal.GetReference(destinationFloats);
        ref var coefficientReference = ref MemoryMarshal.GetArrayDataReference(coefficients.Packed);
        for (; phase < vectorEnd; phase += complexValuesPerVector, destinationIndex += complexValuesPerVector)
        {
            var accumulator = Vector512<float>.Zero;
            var vectorBlock = phase / complexValuesPerVector;
            for (var tap = 0; tap < coefficients.TapCountPerPhase; tap++)
            {
                var firstSampleIndex = newestSpanIndex - phase - (complexValuesPerVector - 1) -
                                       (tap * coefficients.FftSize);
                var samples = Vector512.LoadUnsafe(ref inputReference, (nuint)(firstSampleIndex * 2));
                var coefficientOffset = (tap * coefficients.VectorBlockCount + vectorBlock) * floatsPerVector;
                var packedCoefficients = Vector512.LoadUnsafe(ref coefficientReference, (nuint)coefficientOffset);
                accumulator = Avx512F.FusedMultiplyAdd(samples, packedCoefficients, accumulator);
            }

            var ordered = Avx512F.PermuteVar16x32(accumulator.AsInt32(), ReverseComplexPairs512).AsSingle();
            ordered.StoreUnsafe(ref destinationReference, (nuint)(destinationIndex * 2));
        }

        while (destinationIndex < count)
        {
            destination[destinationIndex++] =
                FilterPhaseScalar(input, prototype, coefficients.FftSize, newestSpanIndex, phase++);
        }
    }

    private static void FillAvx2SegmentSpecialized<TTapKernel>(
        ReadOnlySpan<ComplexF> input,
        ReadOnlySpan<float> prototype,
        Avx2PfbCoefficients coefficients,
        int newestSpanIndex,
        int phaseStart,
        int count,
        Span<ComplexF> destination)
        where TTapKernel : struct, IAvx2TapKernel
    {
        var phase = phaseStart;
        var destinationIndex = 0;
        while (destinationIndex < count && (phase & (ComplexValuesPerVector - 1)) != 0)
        {
            destination[destinationIndex++] =
                FilterPhaseScalar(input, prototype, coefficients.FftSize, newestSpanIndex, phase++);
        }

        var vectorEnd = phase + ((count - destinationIndex) & ~(ComplexValuesPerVector - 1));
        var inputFloats = MemoryMarshal.Cast<ComplexF, float>(input);
        var destinationFloats = MemoryMarshal.Cast<ComplexF, float>(destination);
        ref var inputReference = ref MemoryMarshal.GetReference(inputFloats);
        ref var destinationReference = ref MemoryMarshal.GetReference(destinationFloats);
        ref var coefficientReference = ref MemoryMarshal.GetArrayDataReference(coefficients.Packed);
        var inputTapStride = coefficients.FftSize * 2;
        var coefficientTapStride = coefficients.VectorBlockCount * FloatsPerVector;
        for (; phase < vectorEnd; phase += ComplexValuesPerVector, destinationIndex += ComplexValuesPerVector)
        {
            var firstSampleOffset = (newestSpanIndex - phase - (ComplexValuesPerVector - 1)) * 2;
            var coefficientOffset = (phase / ComplexValuesPerVector) * FloatsPerVector;
            var accumulator = TTapKernel.Accumulate(
                ref inputReference,
                ref coefficientReference,
                firstSampleOffset,
                coefficientOffset,
                inputTapStride,
                coefficientTapStride);
            var ordered = Avx2.PermuteVar8x32(accumulator.AsInt32(), ReverseComplexPairs).AsSingle();
            ordered.StoreUnsafe(ref destinationReference, (nuint)(destinationIndex * 2));
        }

        while (destinationIndex < count)
        {
            destination[destinationIndex++] =
                FilterPhaseScalar(input, prototype, coefficients.FftSize, newestSpanIndex, phase++);
        }
    }

    private static void FillAvx512SegmentSpecialized<TTapKernel>(
        ReadOnlySpan<ComplexF> input,
        ReadOnlySpan<float> prototype,
        Avx512PfbCoefficients coefficients,
        int newestSpanIndex,
        int phaseStart,
        int count,
        Span<ComplexF> destination)
        where TTapKernel : struct, IAvx512TapKernel
    {
        var phase = phaseStart;
        var destinationIndex = 0;
        while (destinationIndex < count && (phase & (ComplexValuesPerVector512 - 1)) != 0)
        {
            destination[destinationIndex++] =
                FilterPhaseScalar(input, prototype, coefficients.FftSize, newestSpanIndex, phase++);
        }

        var vectorEnd = phase + ((count - destinationIndex) & ~(ComplexValuesPerVector512 - 1));
        var inputFloats = MemoryMarshal.Cast<ComplexF, float>(input);
        var destinationFloats = MemoryMarshal.Cast<ComplexF, float>(destination);
        ref var inputReference = ref MemoryMarshal.GetReference(inputFloats);
        ref var destinationReference = ref MemoryMarshal.GetReference(destinationFloats);
        ref var coefficientReference = ref MemoryMarshal.GetArrayDataReference(coefficients.Packed);
        var inputTapStride = coefficients.FftSize * 2;
        var coefficientTapStride = coefficients.VectorBlockCount * FloatsPerVector512;
        for (; phase < vectorEnd; phase += ComplexValuesPerVector512, destinationIndex += ComplexValuesPerVector512)
        {
            var firstSampleOffset = (newestSpanIndex - phase - (ComplexValuesPerVector512 - 1)) * 2;
            var coefficientOffset = (phase / ComplexValuesPerVector512) * FloatsPerVector512;
            var accumulator = TTapKernel.Accumulate(
                ref inputReference,
                ref coefficientReference,
                firstSampleOffset,
                coefficientOffset,
                inputTapStride,
                coefficientTapStride);
            var ordered = Avx512F.PermuteVar16x32(accumulator.AsInt32(), ReverseComplexPairs512).AsSingle();
            ordered.StoreUnsafe(ref destinationReference, (nuint)(destinationIndex * 2));
        }

        while (destinationIndex < count)
        {
            destination[destinationIndex++] =
                FilterPhaseScalar(input, prototype, coefficients.FftSize, newestSpanIndex, phase++);
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

    private interface IAvx2TapKernel
    {
        static abstract Vector256<float> Accumulate(
            ref float inputReference,
            ref float coefficientReference,
            int firstSampleOffset,
            int coefficientOffset,
            int inputTapStride,
            int coefficientTapStride);
    }

    private interface IAvx512TapKernel
    {
        static abstract Vector512<float> Accumulate(
            ref float inputReference,
            ref float coefficientReference,
            int firstSampleOffset,
            int coefficientOffset,
            int inputTapStride,
            int coefficientTapStride);
    }

    private readonly struct Avx2Tap4Kernel : IAvx2TapKernel
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<float> Accumulate(
            ref float inputReference,
            ref float coefficientReference,
            int firstSampleOffset,
            int coefficientOffset,
            int inputTapStride,
            int coefficientTapStride) =>
            AccumulateAvx2Block4(
                ref inputReference,
                ref coefficientReference,
                firstSampleOffset,
                coefficientOffset,
                inputTapStride,
                coefficientTapStride,
                0,
                Vector256<float>.Zero);
    }

    private readonly struct Avx2Tap8Kernel : IAvx2TapKernel
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<float> Accumulate(
            ref float inputReference,
            ref float coefficientReference,
            int firstSampleOffset,
            int coefficientOffset,
            int inputTapStride,
            int coefficientTapStride)
        {
            var accumulator = AccumulateAvx2Block4(
                ref inputReference,
                ref coefficientReference,
                firstSampleOffset,
                coefficientOffset,
                inputTapStride,
                coefficientTapStride,
                0,
                Vector256<float>.Zero);
            return AccumulateAvx2Block4(
                ref inputReference,
                ref coefficientReference,
                firstSampleOffset,
                coefficientOffset,
                inputTapStride,
                coefficientTapStride,
                4,
                accumulator);
        }
    }

    private readonly struct Avx2Tap12Kernel : IAvx2TapKernel
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<float> Accumulate(
            ref float inputReference,
            ref float coefficientReference,
            int firstSampleOffset,
            int coefficientOffset,
            int inputTapStride,
            int coefficientTapStride)
        {
            var accumulator = Avx2Tap8Kernel.Accumulate(
                ref inputReference,
                ref coefficientReference,
                firstSampleOffset,
                coefficientOffset,
                inputTapStride,
                coefficientTapStride);
            return AccumulateAvx2Block4(
                ref inputReference,
                ref coefficientReference,
                firstSampleOffset,
                coefficientOffset,
                inputTapStride,
                coefficientTapStride,
                8,
                accumulator);
        }
    }

    private readonly struct Avx2Tap16Kernel : IAvx2TapKernel
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<float> Accumulate(
            ref float inputReference,
            ref float coefficientReference,
            int firstSampleOffset,
            int coefficientOffset,
            int inputTapStride,
            int coefficientTapStride)
        {
            var accumulator = Avx2Tap12Kernel.Accumulate(
                ref inputReference,
                ref coefficientReference,
                firstSampleOffset,
                coefficientOffset,
                inputTapStride,
                coefficientTapStride);
            return AccumulateAvx2Block4(
                ref inputReference,
                ref coefficientReference,
                firstSampleOffset,
                coefficientOffset,
                inputTapStride,
                coefficientTapStride,
                12,
                accumulator);
        }
    }

    private readonly struct Avx512Tap4Kernel : IAvx512TapKernel
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector512<float> Accumulate(
            ref float inputReference,
            ref float coefficientReference,
            int firstSampleOffset,
            int coefficientOffset,
            int inputTapStride,
            int coefficientTapStride) =>
            AccumulateAvx512Block4(
                ref inputReference,
                ref coefficientReference,
                firstSampleOffset,
                coefficientOffset,
                inputTapStride,
                coefficientTapStride,
                0,
                Vector512<float>.Zero);
    }

    private readonly struct Avx512Tap8Kernel : IAvx512TapKernel
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector512<float> Accumulate(
            ref float inputReference,
            ref float coefficientReference,
            int firstSampleOffset,
            int coefficientOffset,
            int inputTapStride,
            int coefficientTapStride)
        {
            var accumulator = AccumulateAvx512Block4(
                ref inputReference,
                ref coefficientReference,
                firstSampleOffset,
                coefficientOffset,
                inputTapStride,
                coefficientTapStride,
                0,
                Vector512<float>.Zero);
            return AccumulateAvx512Block4(
                ref inputReference,
                ref coefficientReference,
                firstSampleOffset,
                coefficientOffset,
                inputTapStride,
                coefficientTapStride,
                4,
                accumulator);
        }
    }

    private readonly struct Avx512Tap12Kernel : IAvx512TapKernel
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector512<float> Accumulate(
            ref float inputReference,
            ref float coefficientReference,
            int firstSampleOffset,
            int coefficientOffset,
            int inputTapStride,
            int coefficientTapStride)
        {
            var accumulator = Avx512Tap8Kernel.Accumulate(
                ref inputReference,
                ref coefficientReference,
                firstSampleOffset,
                coefficientOffset,
                inputTapStride,
                coefficientTapStride);
            return AccumulateAvx512Block4(
                ref inputReference,
                ref coefficientReference,
                firstSampleOffset,
                coefficientOffset,
                inputTapStride,
                coefficientTapStride,
                8,
                accumulator);
        }
    }

    private readonly struct Avx512Tap16Kernel : IAvx512TapKernel
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector512<float> Accumulate(
            ref float inputReference,
            ref float coefficientReference,
            int firstSampleOffset,
            int coefficientOffset,
            int inputTapStride,
            int coefficientTapStride)
        {
            var accumulator = Avx512Tap12Kernel.Accumulate(
                ref inputReference,
                ref coefficientReference,
                firstSampleOffset,
                coefficientOffset,
                inputTapStride,
                coefficientTapStride);
            return AccumulateAvx512Block4(
                ref inputReference,
                ref coefficientReference,
                firstSampleOffset,
                coefficientOffset,
                inputTapStride,
                coefficientTapStride,
                12,
                accumulator);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> AccumulateAvx2Block4(
        ref float inputReference,
        ref float coefficientReference,
        int firstSampleOffset,
        int coefficientOffset,
        int inputTapStride,
        int coefficientTapStride,
        int tapOffset,
        Vector256<float> accumulator)
    {
        accumulator = Fma.MultiplyAdd(
            Vector256.LoadUnsafe(ref inputReference, (nuint)(firstSampleOffset - (tapOffset * inputTapStride))),
            Vector256.LoadUnsafe(ref coefficientReference, (nuint)(coefficientOffset + (tapOffset * coefficientTapStride))),
            accumulator);
        accumulator = Fma.MultiplyAdd(
            Vector256.LoadUnsafe(ref inputReference, (nuint)(firstSampleOffset - ((tapOffset + 1) * inputTapStride))),
            Vector256.LoadUnsafe(ref coefficientReference, (nuint)(coefficientOffset + ((tapOffset + 1) * coefficientTapStride))),
            accumulator);
        accumulator = Fma.MultiplyAdd(
            Vector256.LoadUnsafe(ref inputReference, (nuint)(firstSampleOffset - ((tapOffset + 2) * inputTapStride))),
            Vector256.LoadUnsafe(ref coefficientReference, (nuint)(coefficientOffset + ((tapOffset + 2) * coefficientTapStride))),
            accumulator);
        return Fma.MultiplyAdd(
            Vector256.LoadUnsafe(ref inputReference, (nuint)(firstSampleOffset - ((tapOffset + 3) * inputTapStride))),
            Vector256.LoadUnsafe(ref coefficientReference, (nuint)(coefficientOffset + ((tapOffset + 3) * coefficientTapStride))),
            accumulator);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<float> AccumulateAvx512Block4(
        ref float inputReference,
        ref float coefficientReference,
        int firstSampleOffset,
        int coefficientOffset,
        int inputTapStride,
        int coefficientTapStride,
        int tapOffset,
        Vector512<float> accumulator)
    {
        accumulator = Avx512F.FusedMultiplyAdd(
            Vector512.LoadUnsafe(ref inputReference, (nuint)(firstSampleOffset - (tapOffset * inputTapStride))),
            Vector512.LoadUnsafe(ref coefficientReference, (nuint)(coefficientOffset + (tapOffset * coefficientTapStride))),
            accumulator);
        accumulator = Avx512F.FusedMultiplyAdd(
            Vector512.LoadUnsafe(ref inputReference, (nuint)(firstSampleOffset - ((tapOffset + 1) * inputTapStride))),
            Vector512.LoadUnsafe(ref coefficientReference, (nuint)(coefficientOffset + ((tapOffset + 1) * coefficientTapStride))),
            accumulator);
        accumulator = Avx512F.FusedMultiplyAdd(
            Vector512.LoadUnsafe(ref inputReference, (nuint)(firstSampleOffset - ((tapOffset + 2) * inputTapStride))),
            Vector512.LoadUnsafe(ref coefficientReference, (nuint)(coefficientOffset + ((tapOffset + 2) * coefficientTapStride))),
            accumulator);
        return Avx512F.FusedMultiplyAdd(
            Vector512.LoadUnsafe(ref inputReference, (nuint)(firstSampleOffset - ((tapOffset + 3) * inputTapStride))),
            Vector512.LoadUnsafe(ref coefficientReference, (nuint)(coefficientOffset + ((tapOffset + 3) * coefficientTapStride))),
            accumulator);
    }

    private static void ValidateAvx2(
        ReadOnlySpan<ComplexF> input,
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
    }

    private static void ValidateAvx512(
        ReadOnlySpan<ComplexF> input,
        int hopSize,
        int frames,
        ReadOnlySpan<float> prototype,
        Avx512PfbCoefficients coefficients,
        Span<ComplexF> destination)
    {
        Validate(input, hopSize, frames, coefficients.FftSize, prototype, destination);
        if (!Avx512F.IsSupported)
        {
            throw new PlatformNotSupportedException("AVX-512F is required by the PFB kernel.");
        }

        if (prototype.Length / coefficients.FftSize != coefficients.TapCountPerPhase)
        {
            throw new ArgumentException("Packed AVX-512 coefficients do not match the prototype.", nameof(coefficients));
        }
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
