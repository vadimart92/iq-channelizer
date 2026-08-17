using IqChannelizer.Abstractions;

namespace IqChannelizer.Dsp;

internal static class ScalarRotator
{
    public static void RotateInPlace(
        Span<ComplexF> samples,
        double frequencyHz,
        double inputSampleRateHz,
        long firstAbsoluteInputSampleIndex,
        int inputSamplesPerOutputSample)
    {
        if (frequencyHz == 0)
        {
            return;
        }

        var firstPhase = -2 * Math.PI * frequencyHz * firstAbsoluteInputSampleIndex / inputSampleRateHz;
        var stepPhase = -2 * Math.PI * frequencyHz * inputSamplesPerOutputSample / inputSampleRateHz;
        var phasor = ComplexF.FromPolar(firstPhase);
        var step = ComplexF.FromPolar(stepPhase);
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = samples[index] * phasor;
            phasor = phasor * step;
            if ((index & 1023) == 1023)
            {
                phasor = ComplexF.FromPolar(firstPhase + ((index + 1) * stepPhase));
            }
        }
    }
}
