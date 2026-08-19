using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Dsp;

internal static class Avx512ComplexKernels
{
    private const int ComplexValuesPerVector = 8;
    private const int FloatsPerVector = 16;

    public static void CopyScale(ReadOnlySpan<ComplexF> source, float scale, Span<ComplexF> destination)
    {
        ValidateUnary(source, destination);
        EnsureSupported();
        var sourceFloats = MemoryMarshal.Cast<ComplexF, float>(source);
        var destinationFloats = MemoryMarshal.Cast<ComplexF, float>(destination);
        var vectorScale = Vector512.Create(scale);
        var index = 0;
        ref var sourceReference = ref MemoryMarshal.GetReference(sourceFloats);
        ref var destinationReference = ref MemoryMarshal.GetReference(destinationFloats);
        for (; index <= sourceFloats.Length - FloatsPerVector; index += FloatsPerVector)
        {
            var values = Vector512.LoadUnsafe(ref sourceReference, (nuint)index);
            Avx512F.Multiply(values, vectorScale).StoreUnsafe(ref destinationReference, (nuint)index);
        }

        for (; index < sourceFloats.Length; index++)
        {
            destinationFloats[index] = sourceFloats[index] * scale;
        }
    }

    public static void MultiplyComplex(
        ReadOnlySpan<ComplexF> left,
        ReadOnlySpan<ComplexF> right,
        Span<ComplexF> destination)
    {
        ValidateBinary(left, right, destination);
        EnsureSupported();
        var leftFloats = MemoryMarshal.Cast<ComplexF, float>(left);
        var rightFloats = MemoryMarshal.Cast<ComplexF, float>(right);
        var destinationFloats = MemoryMarshal.Cast<ComplexF, float>(destination);
        var index = 0;
        ref var leftReference = ref MemoryMarshal.GetReference(leftFloats);
        ref var rightReference = ref MemoryMarshal.GetReference(rightFloats);
        ref var destinationReference = ref MemoryMarshal.GetReference(destinationFloats);
        for (; index <= leftFloats.Length - FloatsPerVector; index += FloatsPerVector)
        {
            var leftValues = Vector512.LoadUnsafe(ref leftReference, (nuint)index);
            var rightValues = Vector512.LoadUnsafe(ref rightReference, (nuint)index);
            MultiplyComplex(leftValues, rightValues).StoreUnsafe(ref destinationReference, (nuint)index);
        }

        for (var complexIndex = index / 2; complexIndex < left.Length; complexIndex++)
        {
            destination[complexIndex] = left[complexIndex] * right[complexIndex];
        }
    }

    public static void MultiplyComplexByScalar(
        ReadOnlySpan<ComplexF> source,
        ComplexF factor,
        Span<ComplexF> destination)
    {
        ValidateUnary(source, destination);
        EnsureSupported();
        var sourceFloats = MemoryMarshal.Cast<ComplexF, float>(source);
        var destinationFloats = MemoryMarshal.Cast<ComplexF, float>(destination);
        var factorVector = Vector512.Create(
            factor.Real, factor.Imaginary, factor.Real, factor.Imaginary,
            factor.Real, factor.Imaginary, factor.Real, factor.Imaginary,
            factor.Real, factor.Imaginary, factor.Real, factor.Imaginary,
            factor.Real, factor.Imaginary, factor.Real, factor.Imaginary);
        var index = 0;
        ref var sourceReference = ref MemoryMarshal.GetReference(sourceFloats);
        ref var destinationReference = ref MemoryMarshal.GetReference(destinationFloats);
        for (; index <= sourceFloats.Length - FloatsPerVector; index += FloatsPerVector)
        {
            var values = Vector512.LoadUnsafe(ref sourceReference, (nuint)index);
            MultiplyComplex(values, factorVector).StoreUnsafe(ref destinationReference, (nuint)index);
        }

        for (var complexIndex = index / 2; complexIndex < source.Length; complexIndex++)
        {
            destination[complexIndex] = source[complexIndex] * factor;
        }
    }

    public static void MultiplyComplexByReal(
        ReadOnlySpan<ComplexF> source,
        ReadOnlySpan<float> factors,
        Span<ComplexF> destination)
    {
        if (source.Length != factors.Length)
        {
            throw new ArgumentException("Complex source and real-factor lengths must match.", nameof(factors));
        }

        ValidateUnary(source, destination);
        EnsureSupported();
        var sourceFloats = MemoryMarshal.Cast<ComplexF, float>(source);
        var destinationFloats = MemoryMarshal.Cast<ComplexF, float>(destination);
        var complexIndex = 0;
        var floatIndex = 0;
        ref var sourceReference = ref MemoryMarshal.GetReference(sourceFloats);
        ref var destinationReference = ref MemoryMarshal.GetReference(destinationFloats);
        for (; complexIndex <= source.Length - ComplexValuesPerVector;
             complexIndex += ComplexValuesPerVector, floatIndex += FloatsPerVector)
        {
            var factorVector = Vector512.Create(
                factors[complexIndex], factors[complexIndex],
                factors[complexIndex + 1], factors[complexIndex + 1],
                factors[complexIndex + 2], factors[complexIndex + 2],
                factors[complexIndex + 3], factors[complexIndex + 3],
                factors[complexIndex + 4], factors[complexIndex + 4],
                factors[complexIndex + 5], factors[complexIndex + 5],
                factors[complexIndex + 6], factors[complexIndex + 6],
                factors[complexIndex + 7], factors[complexIndex + 7]);
            var values = Vector512.LoadUnsafe(ref sourceReference, (nuint)floatIndex);
            Avx512F.Multiply(values, factorVector).StoreUnsafe(ref destinationReference, (nuint)floatIndex);
        }

        for (; complexIndex < source.Length; complexIndex++)
        {
            destination[complexIndex] = source[complexIndex] * factors[complexIndex];
        }
    }

    public static void Add(
        ReadOnlySpan<ComplexF> left,
        ReadOnlySpan<ComplexF> right,
        Span<ComplexF> destination)
    {
        ValidateBinary(left, right, destination);
        EnsureSupported();
        var leftFloats = MemoryMarshal.Cast<ComplexF, float>(left);
        var rightFloats = MemoryMarshal.Cast<ComplexF, float>(right);
        var destinationFloats = MemoryMarshal.Cast<ComplexF, float>(destination);
        var index = 0;
        ref var leftReference = ref MemoryMarshal.GetReference(leftFloats);
        ref var rightReference = ref MemoryMarshal.GetReference(rightFloats);
        ref var destinationReference = ref MemoryMarshal.GetReference(destinationFloats);
        for (; index <= leftFloats.Length - FloatsPerVector; index += FloatsPerVector)
        {
            var leftValues = Vector512.LoadUnsafe(ref leftReference, (nuint)index);
            var rightValues = Vector512.LoadUnsafe(ref rightReference, (nuint)index);
            Avx512F.Add(leftValues, rightValues).StoreUnsafe(ref destinationReference, (nuint)index);
        }

        for (var complexIndex = index / 2; complexIndex < left.Length; complexIndex++)
        {
            destination[complexIndex] = left[complexIndex] + right[complexIndex];
        }
    }

    internal static Vector512<float> MultiplyComplex(Vector512<float> left, Vector512<float> right)
    {
        var real = Avx512F.Shuffle(left, left, 0b1010_0000);
        var imaginary = Avx512F.Shuffle(left, left, 0b1111_0101);
        var swappedRight = Avx512F.Shuffle(right, right, 0b1011_0001);
        var imaginaryProduct = Avx512F.Multiply(imaginary, swappedRight);
        return Avx512F.FusedMultiplyAddSubtract(real, right, imaginaryProduct);
    }

    private static void ValidateUnary(ReadOnlySpan<ComplexF> source, Span<ComplexF> destination)
    {
        if (source.Length != destination.Length)
        {
            throw new ArgumentException("Source and destination lengths must match.", nameof(destination));
        }

        if (source.Overlaps(destination, out var offset) && offset != 0)
        {
            throw new ArgumentException("Partial source/destination overlap is not supported.", nameof(destination));
        }
    }

    private static void ValidateBinary(
        ReadOnlySpan<ComplexF> left,
        ReadOnlySpan<ComplexF> right,
        Span<ComplexF> destination)
    {
        if (left.Length != right.Length || left.Length != destination.Length)
        {
            throw new ArgumentException("Input and destination lengths must match.", nameof(destination));
        }

        ValidateUnary(left, destination);
        ValidateUnary(right, destination);
    }

    private static void EnsureSupported()
    {
        if (!Avx512F.IsSupported)
        {
            throw new PlatformNotSupportedException("AVX-512F is required by this kernel.");
        }
    }
}
