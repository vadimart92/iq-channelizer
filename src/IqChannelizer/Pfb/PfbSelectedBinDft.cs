using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;

namespace IqChannelizer.Pfb;

internal sealed class PfbSelectedBinDft
{
    private readonly int _fftSize;
    private readonly int[] _bins;
    private readonly ComplexF[] _twiddles;

    public PfbSelectedBinDft(int fftSize, ReadOnlySpan<int> bins)
    {
        if (fftSize < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(fftSize));
        }

        if (bins.IsEmpty)
        {
            throw new ArgumentException("At least one selected bin is required.", nameof(bins));
        }

        _fftSize = fftSize;
        _bins = bins.ToArray();
        _twiddles = new ComplexF[checked(fftSize * bins.Length)];
        for (var binIndex = 0; binIndex < _bins.Length; binIndex++)
        {
            var bin = _bins[binIndex];
            if ((uint)bin >= (uint)fftSize)
            {
                throw new ArgumentOutOfRangeException(nameof(bins), "Selected bins must be normalized FFT indices.");
            }

            for (var phase = 0; phase < fftSize; phase++)
            {
                _twiddles[(binIndex * fftSize) + phase] =
                    ComplexF.FromPolar(2 * Math.PI * bin * phase / fftSize);
            }
        }
    }

    public int SelectedBinCount => _bins.Length;

    public void TransformScalar(
        ReadOnlySpan<ComplexF> input,
        int frames,
        Span<ComplexF> destination)
    {
        Validate(input, frames, destination);
        for (var binIndex = 0; binIndex < _bins.Length; binIndex++)
        {
            var twiddles = _twiddles.AsSpan(binIndex * _fftSize, _fftSize);
            var binDestination = destination.Slice(binIndex * frames, frames);
            for (var frame = 0; frame < frames; frame++)
            {
                var values = input.Slice(frame * _fftSize, _fftSize);
                var accumulator = new ComplexF();
                for (var phase = 0; phase < _fftSize; phase++)
                {
                    accumulator += values[phase] * twiddles[phase];
                }

                binDestination[frame] = accumulator;
            }
        }
    }

    public void TransformAvx2(
        ReadOnlySpan<ComplexF> input,
        int frames,
        Span<ComplexF> destination)
    {
        Validate(input, frames, destination);
        if (!Avx2.IsSupported || !Fma.IsSupported)
        {
            throw new PlatformNotSupportedException("AVX2/FMA is required by this selected-bin DFT kernel.");
        }

        for (var binIndex = 0; binIndex < _bins.Length; binIndex++)
        {
            var twiddles = _twiddles.AsSpan(binIndex * _fftSize, _fftSize);
            var binDestination = destination.Slice(binIndex * frames, frames);
            for (var frame = 0; frame < frames; frame++)
            {
                binDestination[frame] = DotAvx2(input.Slice(frame * _fftSize, _fftSize), twiddles);
            }
        }
    }

    public void TransformAvx512(
        ReadOnlySpan<ComplexF> input,
        int frames,
        Span<ComplexF> destination)
    {
        Validate(input, frames, destination);
        if (!Avx512F.IsSupported)
        {
            throw new PlatformNotSupportedException("AVX-512F is required by this selected-bin DFT kernel.");
        }

        for (var binIndex = 0; binIndex < _bins.Length; binIndex++)
        {
            var twiddles = _twiddles.AsSpan(binIndex * _fftSize, _fftSize);
            var binDestination = destination.Slice(binIndex * frames, frames);
            for (var frame = 0; frame < frames; frame++)
            {
                binDestination[frame] = DotAvx512(input.Slice(frame * _fftSize, _fftSize), twiddles);
            }
        }
    }

    private static ComplexF DotAvx2(ReadOnlySpan<ComplexF> values, ReadOnlySpan<ComplexF> twiddles)
    {
        const int complexValuesPerVector = 4;
        var valueFloats = MemoryMarshal.Cast<ComplexF, float>(values);
        var twiddleFloats = MemoryMarshal.Cast<ComplexF, float>(twiddles);
        ref var valueReference = ref MemoryMarshal.GetReference(valueFloats);
        ref var twiddleReference = ref MemoryMarshal.GetReference(twiddleFloats);
        var accumulator = Vector256<float>.Zero;
        var phase = 0;
        for (; phase <= values.Length - complexValuesPerVector; phase += complexValuesPerVector)
        {
            var floatIndex = phase * 2;
            var valueVector = Vector256.LoadUnsafe(ref valueReference, (nuint)floatIndex);
            var twiddleVector = Vector256.LoadUnsafe(ref twiddleReference, (nuint)floatIndex);
            accumulator = Avx.Add(accumulator, Avx2ComplexKernels.MultiplyComplex(valueVector, twiddleVector));
        }

        var result = new ComplexF(
            accumulator.GetElement(0) + accumulator.GetElement(2) +
            accumulator.GetElement(4) + accumulator.GetElement(6),
            accumulator.GetElement(1) + accumulator.GetElement(3) +
            accumulator.GetElement(5) + accumulator.GetElement(7));
        for (; phase < values.Length; phase++)
        {
            result += values[phase] * twiddles[phase];
        }

        return result;
    }

    private static ComplexF DotAvx512(ReadOnlySpan<ComplexF> values, ReadOnlySpan<ComplexF> twiddles)
    {
        const int complexValuesPerVector = 8;
        var valueFloats = MemoryMarshal.Cast<ComplexF, float>(values);
        var twiddleFloats = MemoryMarshal.Cast<ComplexF, float>(twiddles);
        ref var valueReference = ref MemoryMarshal.GetReference(valueFloats);
        ref var twiddleReference = ref MemoryMarshal.GetReference(twiddleFloats);
        var accumulator = Vector512<float>.Zero;
        var phase = 0;
        for (; phase <= values.Length - complexValuesPerVector; phase += complexValuesPerVector)
        {
            var floatIndex = phase * 2;
            var valueVector = Vector512.LoadUnsafe(ref valueReference, (nuint)floatIndex);
            var twiddleVector = Vector512.LoadUnsafe(ref twiddleReference, (nuint)floatIndex);
            accumulator = Avx512F.Add(
                accumulator,
                Avx512ComplexKernels.MultiplyComplex(valueVector, twiddleVector));
        }

        var result = new ComplexF(
            accumulator.GetElement(0) + accumulator.GetElement(2) +
            accumulator.GetElement(4) + accumulator.GetElement(6) +
            accumulator.GetElement(8) + accumulator.GetElement(10) +
            accumulator.GetElement(12) + accumulator.GetElement(14),
            accumulator.GetElement(1) + accumulator.GetElement(3) +
            accumulator.GetElement(5) + accumulator.GetElement(7) +
            accumulator.GetElement(9) + accumulator.GetElement(11) +
            accumulator.GetElement(13) + accumulator.GetElement(15));
        for (; phase < values.Length; phase++)
        {
            result += values[phase] * twiddles[phase];
        }

        return result;
    }

    private void Validate(ReadOnlySpan<ComplexF> input, int frames, Span<ComplexF> destination)
    {
        if (frames <= 0 || input.Length != checked(frames * _fftSize))
        {
            throw new ArgumentException("Selected-bin DFT input must contain exactly one K-vector per frame.", nameof(input));
        }

        if (destination.Length != checked(frames * _bins.Length))
        {
            throw new ArgumentException("Selected-bin DFT destination must contain one stream per selected bin.", nameof(destination));
        }

        if (input.Overlaps(destination))
        {
            throw new ArgumentException("Selected-bin DFT input and destination must not overlap.", nameof(destination));
        }
    }
}
