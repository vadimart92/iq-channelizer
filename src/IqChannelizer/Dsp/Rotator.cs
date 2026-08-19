using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Dsp;

internal sealed class Rotator
{
    private const int NormalizeInterval = 16 * 1024;
    private const int Avx2ComplexValuesPerVector = 4;
    private const int Avx2Unroll = 4;
    private const int Avx2SamplesPerUnrolledLoop = Avx2ComplexValuesPerVector * Avx2Unroll;

    private readonly double _frequencyHz;
    private readonly double _sampleRateHz;
    private readonly DoublePhasor _step;
    private readonly DoublePhasor _step4;
    private readonly DoublePhasor _step16;
    private readonly Vector256<float> _offset0;
    private readonly Vector256<float> _offset1;
    private readonly Vector256<float> _offset2;
    private readonly Vector256<float> _offset3;
    private readonly bool _useAvx2;
    private DoublePhasor _phase = new(1, 0);
    private Vector256<float> _basePhasor;

    public Rotator(
        double frequencyHz,
        double sampleRateHz,
        int inputSamplesPerOutputSample,
        SimdPreference backend = SimdPreference.Scalar)
    {
        _frequencyHz = frequencyHz;
        _sampleRateHz = sampleRateHz;
        _useAvx2 = backend == SimdPreference.Avx2;
        if (_useAvx2 && (!Avx2.IsSupported || !Fma.IsSupported))
        {
            throw new PlatformNotSupportedException("AVX2/FMA is required by the residual rotator kernel.");
        }

        _step = DoublePhasor.Create(frequencyHz, sampleRateHz, inputSamplesPerOutputSample);
        _step4 = _step.Pow(Avx2ComplexValuesPerVector);
        _step16 = _step4.Pow(Avx2Unroll);
        if (_useAvx2)
        {
            _offset0 = CreateAvx2LaneOffsets(_step, 0);
            _offset1 = CreateAvx2LaneOffsets(_step, Avx2ComplexValuesPerVector);
            _offset2 = CreateAvx2LaneOffsets(_step, Avx2ComplexValuesPerVector * 2);
            _offset3 = CreateAvx2LaneOffsets(_step, Avx2ComplexValuesPerVector * 3);
        }
    }

    internal static ComplexF CreatePhasor(double frequencyHz, double sampleRateHz, long absoluteSampleIndex)
    {
        var phasor = DoublePhasor.Create(frequencyHz, sampleRateHz, absoluteSampleIndex);
        return new ComplexF((float)phasor.Real, (float)phasor.Imaginary);
    }
    
    public void SetPhase(float phase)
    {
        var (sine, cosine) = MathF.SinCos(phase);
        _phase = new DoublePhasor(cosine, sine).Normalize();
        _basePhasor = CreateRepeatedAvx2Phasor(_phase);
    }

    public void SetPhaseFromAbsoluteIndex(long absoluteSampleIndex) =>
        SetPhase(DoublePhasor.CreatePhaseRadians(_frequencyHz, _sampleRateHz, absoluteSampleIndex));

    public void RotateInPlace(Span<ComplexF> samples)
    {
        if (_frequencyHz == 0 || samples.IsEmpty)
        {
            return;
        }

        if (_useAvx2)
        {
            RotateInPlaceAvx2(samples);
        }
        else
        {
            RotateInPlaceScalar(samples);
        }
    }

    private void RotateInPlaceScalar(Span<ComplexF> samples)
    {
        var phase = _phase;
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = phase.Multiply(samples[index]);
            phase *= _step;
            if ((index & (NormalizeInterval - 1)) == NormalizeInterval - 1)
            {
                phase = phase.Normalize();
            }
        }

        _phase = phase;
        _basePhasor = CreateRepeatedAvx2Phasor(_phase);
    }

    private void RotateInPlaceAvx2(Span<ComplexF> samples)
    {
        var phase = _phase;
        var basePhasor = _basePhasor;
        var sampleFloats = MemoryMarshal.Cast<ComplexF, float>(samples);
        ref var sampleReference = ref MemoryMarshal.GetReference(sampleFloats);
        var index = 0;
        for (; index <= samples.Length - Avx2SamplesPerUnrolledLoop; index += Avx2SamplesPerUnrolledLoop)
        {
            RotateBatch(ref sampleReference, index, basePhasor, _offset0);
            RotateBatch(ref sampleReference, index + 4, basePhasor, _offset1);
            RotateBatch(ref sampleReference, index + 8, basePhasor, _offset2);
            RotateBatch(ref sampleReference, index + 12, basePhasor, _offset3);

            phase *= _step16;
            if (((index + Avx2SamplesPerUnrolledLoop) & (NormalizeInterval - 1)) == 0)
            {
                phase = phase.Normalize();
            }

            basePhasor = CreateRepeatedAvx2Phasor(phase);
        }

        for (; index <= samples.Length - Avx2ComplexValuesPerVector; index += Avx2ComplexValuesPerVector)
        {
            RotateBatch(ref sampleReference, index, basePhasor, _offset0);
            phase *= _step4;
            if ((index & (NormalizeInterval - 1)) == NormalizeInterval - Avx2ComplexValuesPerVector)
            {
                phase = phase.Normalize();
            }

            basePhasor = CreateRepeatedAvx2Phasor(phase);
        }

        for (; index < samples.Length; index++)
        {
            samples[index] = phase.Multiply(samples[index]);
            phase *= _step;
            if ((index & (NormalizeInterval - 1)) == NormalizeInterval - 1)
            {
                phase = phase.Normalize();
            }
        }

        _phase = phase;
        _basePhasor = CreateRepeatedAvx2Phasor(_phase);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RotateBatch(
        ref float sampleReference,
        int complexIndex,
        Vector256<float> basePhasor,
        Vector256<float> laneOffsets)
    {
        var phasorVector = Avx2ComplexKernels.MultiplyComplex(basePhasor, laneOffsets);
        var values = Vector256.LoadUnsafe(ref sampleReference, (nuint)(complexIndex * 2));
        Avx2ComplexKernels.MultiplyComplex(values, phasorVector)
            .StoreUnsafe(ref sampleReference, (nuint)(complexIndex * 2));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> CreateRepeatedAvx2Phasor(DoublePhasor phasor) => Vector256.Create(
        (float)phasor.Real, (float)phasor.Imaginary,
        (float)phasor.Real, (float)phasor.Imaginary,
        (float)phasor.Real, (float)phasor.Imaginary,
        (float)phasor.Real, (float)phasor.Imaginary);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> CreateAvx2LaneOffsets(DoublePhasor step, int firstLane)
    {
        var lane0 = step.Pow(firstLane);
        var lane1 = lane0 * step;
        var lane2 = lane1 * step;
        var lane3 = lane2 * step;
        return Vector256.Create(
            (float)lane0.Real, (float)lane0.Imaginary,
            (float)lane1.Real, (float)lane1.Imaginary,
            (float)lane2.Real, (float)lane2.Imaginary,
            (float)lane3.Real, (float)lane3.Imaginary);
    }
}