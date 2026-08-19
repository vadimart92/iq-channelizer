using IqChannelizer.Abstractions;

namespace IqChannelizer.Dsp;

internal abstract class Rotator(double frequencyHz, double sampleRateHz, int inputSamplesPerOutputSample)
{
    protected const int NormalizeInterval = 16 * 1024;

    protected DoublePhasor Step { get; } = DoublePhasor.Create(frequencyHz, sampleRateHz, inputSamplesPerOutputSample);
    protected DoublePhasor Phase { get; set; } = new(1, 0);

    public static Rotator Create(
        double frequencyHz,
        double sampleRateHz,
        int inputSamplesPerOutputSample,
        SimdPreference backend = SimdPreference.Scalar)
    {
        if (frequencyHz == 0)
        {
            return new NoOpRotator(frequencyHz, sampleRateHz, inputSamplesPerOutputSample);
        }

        return backend == SimdPreference.Avx2
            ? new Avx2ResidualRotator(frequencyHz, sampleRateHz, inputSamplesPerOutputSample)
            : new ScalarResidualRotator(frequencyHz, sampleRateHz, inputSamplesPerOutputSample);
    }

    internal static ComplexF CreatePhasor(double frequencyHz, double sampleRateHz, long absoluteSampleIndex)
    {
        var phasor = DoublePhasor.Create(frequencyHz, sampleRateHz, absoluteSampleIndex);
        return new ComplexF((float)phasor.Real, (float)phasor.Imaginary);
    }

    public void SetPhase(float phase)
    {
        var (sine, cosine) = MathF.SinCos(phase);
        Phase = new DoublePhasor(cosine, sine).Normalize();
        PhaseChanged();
    }

    public void SetPhaseFromAbsoluteIndex(long absoluteSampleIndex) =>
        SetPhase(DoublePhasor.CreatePhaseRadians(frequencyHz, sampleRateHz, absoluteSampleIndex));

    public abstract void RotateInPlace(Span<ComplexF> samples);

    protected virtual void PhaseChanged()
    {
    }
}