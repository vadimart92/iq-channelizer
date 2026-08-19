using IqChannelizer.Abstractions;

namespace IqChannelizer.Dsp;

internal sealed class ScalarResidualRotator(double frequencyHz, double sampleRateHz, int inputSamplesPerOutputSample)
    : Rotator(frequencyHz, sampleRateHz, inputSamplesPerOutputSample)
{
    public override void RotateInPlace(Span<ComplexF> samples)
    {
        var phase = Phase;
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = phase.Multiply(samples[index]);
            phase *= Step;
            if ((index & (NormalizeInterval - 1)) == NormalizeInterval - 1)
            {
                phase = phase.Normalize();
            }
        }

        Phase = phase;
    }
}