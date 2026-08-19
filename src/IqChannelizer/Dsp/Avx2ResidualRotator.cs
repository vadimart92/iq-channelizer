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
    private readonly Vector256<float> _swappedOffset0;
    private readonly Vector256<float> _swappedOffset1;
    private readonly Vector256<float> _swappedOffset2;
    private readonly Vector256<float> _swappedOffset3;
    private Vector256<float> _baseReal;
    private Vector256<float> _baseImaginary;

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
        _swappedOffset0 = Avx.Permute(_offset0, 0b1011_0001);
        _swappedOffset1 = Avx.Permute(_offset1, 0b1011_0001);
        _swappedOffset2 = Avx.Permute(_offset2, 0b1011_0001);
        _swappedOffset3 = Avx.Permute(_offset3, 0b1011_0001);
        _baseReal = CreateRepeatedReal(Phase);
        _baseImaginary = CreateRepeatedImaginary(Phase);
    }

    public override void RotateInPlace(Span<ComplexF> samples)
    {
        var phase = Phase;
        var baseReal = _baseReal;
        var baseImaginary = _baseImaginary;
        var sampleFloats = MemoryMarshal.Cast<ComplexF, float>(samples);
        ref var sampleReference = ref MemoryMarshal.GetReference(sampleFloats);
        var index = 0;
        for (; index <= samples.Length - SamplesPerUnrolledLoop; index += SamplesPerUnrolledLoop)
        {
            RotateBatch(ref sampleReference, index, baseReal, baseImaginary, _offset0, _swappedOffset0);
            RotateBatch(ref sampleReference, index + 4, baseReal, baseImaginary, _offset1, _swappedOffset1);
            RotateBatch(ref sampleReference, index + 8, baseReal, baseImaginary, _offset2, _swappedOffset2);
            RotateBatch(ref sampleReference, index + 12, baseReal, baseImaginary, _offset3, _swappedOffset3);

            phase *= _step16;
            if (((index + SamplesPerUnrolledLoop) & (NormalizeInterval - 1)) == 0)
            {
                phase = phase.Normalize();
            }

            baseReal = CreateRepeatedReal(phase);
            baseImaginary = CreateRepeatedImaginary(phase);
        }

        for (; index <= samples.Length - ComplexValuesPerVector; index += ComplexValuesPerVector)
        {
            RotateBatch(ref sampleReference, index, baseReal, baseImaginary, _offset0, _swappedOffset0);
            phase *= _step4;
            if ((index & (NormalizeInterval - 1)) == NormalizeInterval - ComplexValuesPerVector)
            {
                phase = phase.Normalize();
            }

            baseReal = CreateRepeatedReal(phase);
            baseImaginary = CreateRepeatedImaginary(phase);
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
        _baseReal = CreateRepeatedReal(Phase);
        _baseImaginary = CreateRepeatedImaginary(Phase);
    }

    protected override void PhaseChanged()
    {
        _baseReal = CreateRepeatedReal(Phase);
        _baseImaginary = CreateRepeatedImaginary(Phase);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RotateBatch(
        ref float sampleReference,
        int complexIndex,
        Vector256<float> baseReal,
        Vector256<float> baseImaginary,
        Vector256<float> laneOffsets,
        Vector256<float> swappedLaneOffsets)
    {
        var phasorVector =
            Fma.MultiplyAddSubtract(baseReal, laneOffsets, Avx.Multiply(baseImaginary, swappedLaneOffsets));
        var index = (nuint)(complexIndex * 2);
        var values = Vector256.LoadUnsafe(ref sampleReference, index);
        Avx2ComplexKernels.MultiplyComplex(values, phasorVector).StoreUnsafe(ref sampleReference, index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> CreateRepeatedReal(DoublePhasor phasor) => Vector256.Create((float)phasor.Real);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> CreateRepeatedImaginary(DoublePhasor phasor) => Vector256.Create((float)phasor.Imaginary);

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
