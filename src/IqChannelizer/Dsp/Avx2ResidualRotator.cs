using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using IqChannelizer.Abstractions;

namespace IqChannelizer.Dsp;

internal sealed class Avx2ResidualRotator : Rotator
{
    private const int ComplexValuesPerVector = 4;
    private const int Unroll = 4;
    private const int SamplesPerUnrolledLoop = ComplexValuesPerVector * Unroll;

    private readonly DoublePhasor _step4;
    private readonly DoublePhasor _step16;
    private readonly Vector256<float> _offset0;
    private readonly Vector256<float> _offset1;
    private readonly Vector256<float> _offset2;
    private readonly Vector256<float> _offset3;
    private Vector256<float> _basePhasor;

    public Avx2ResidualRotator(double frequencyHz, double sampleRateHz, int inputSamplesPerOutputSample)
        : base(frequencyHz, sampleRateHz, inputSamplesPerOutputSample)
    {
        if (!Avx2.IsSupported || !Fma.IsSupported)
        {
            throw new PlatformNotSupportedException("AVX2/FMA is required by the residual rotator kernel.");
        }

        _step4 = Step.Pow(ComplexValuesPerVector);
        _step16 = _step4.Pow(Unroll);
        _offset0 = CreateAvx2LaneOffsets(Step, 0);
        _offset1 = CreateAvx2LaneOffsets(Step, ComplexValuesPerVector);
        _offset2 = CreateAvx2LaneOffsets(Step, ComplexValuesPerVector * 2);
        _offset3 = CreateAvx2LaneOffsets(Step, ComplexValuesPerVector * 3);
        _basePhasor = CreateRepeatedAvx2Phasor(Phase);
    }

    public override void RotateInPlace(Span<ComplexF> samples)
    {
        var phase = Phase;
        var basePhasor = _basePhasor;
        var sampleFloats = MemoryMarshal.Cast<ComplexF, float>(samples);
        ref var sampleReference = ref MemoryMarshal.GetReference(sampleFloats);
        var index = 0;
        for (; index <= samples.Length - SamplesPerUnrolledLoop; index += SamplesPerUnrolledLoop)
        {
            RotateBatch(ref sampleReference, index, basePhasor, _offset0);
            RotateBatch(ref sampleReference, index + 4, basePhasor, _offset1);
            RotateBatch(ref sampleReference, index + 8, basePhasor, _offset2);
            RotateBatch(ref sampleReference, index + 12, basePhasor, _offset3);

            phase *= _step16;
            if (((index + SamplesPerUnrolledLoop) & (NormalizeInterval - 1)) == 0)
            {
                phase = phase.Normalize();
            }

            basePhasor = CreateRepeatedAvx2Phasor(phase);
        }

        for (; index <= samples.Length - ComplexValuesPerVector; index += ComplexValuesPerVector)
        {
            RotateBatch(ref sampleReference, index, basePhasor, _offset0);
            phase *= _step4;
            if ((index & (NormalizeInterval - 1)) == NormalizeInterval - ComplexValuesPerVector)
            {
                phase = phase.Normalize();
            }

            basePhasor = CreateRepeatedAvx2Phasor(phase);
        }

        for (; index < samples.Length; index++)
        {
            samples[index] = phase.Multiply(samples[index]);
            phase *= Step;
            if ((index & (NormalizeInterval - 1)) == NormalizeInterval - 1)
            {
                phase = phase.Normalize();
            }
        }

        Phase = phase;
        _basePhasor = CreateRepeatedAvx2Phasor(Phase);
    }

    protected override void PhaseChanged() => _basePhasor = CreateRepeatedAvx2Phasor(Phase);

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