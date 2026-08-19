using IqChannelizer.Abstractions;

namespace IqChannelizer.Dsp;

internal sealed class NoOpRotator(double frequencyHz, double sampleRateHz, int inputSamplesPerOutputSample)
    : Rotator(frequencyHz, sampleRateHz, inputSamplesPerOutputSample)
{
    public override void RotateInPlace(Span<ComplexF> samples)
    {
    }
}