using IqChannelizer.Abstractions;

namespace IqChannelizer.Reference;

internal static class DeterministicSignals
{
    public static ComplexF[] Tone(
        int count,
        double frequencyHz,
        double sampleRateHz,
        long firstSampleIndex = 0,
        double amplitude = 1,
        double phaseRadians = 0)
    {
        ValidateCommon(count, sampleRateHz, amplitude);
        ValidateFrequency(frequencyHz, sampleRateHz);
        if (!double.IsFinite(phaseRadians))
        {
            throw new ArgumentOutOfRangeException(nameof(phaseRadians));
        }

        var result = new ComplexF[count];
        var radiansPerSample = 2 * Math.PI * frequencyHz / sampleRateHz;
        for (var index = 0; index < count; index++)
        {
            var phase = phaseRadians + (radiansPerSample * checked(firstSampleIndex + index));
            result[index] = new ComplexF((float)(amplitude * Math.Cos(phase)), (float)(amplitude * Math.Sin(phase)));
        }

        return result;
    }

    public static ComplexF[] TwoTone(
        int count,
        double firstFrequencyHz,
        double firstAmplitude,
        double secondFrequencyHz,
        double secondAmplitude,
        double sampleRateHz,
        long firstSampleIndex = 0)
    {
        var first = Tone(count, firstFrequencyHz, sampleRateHz, firstSampleIndex, firstAmplitude);
        var second = Tone(count, secondFrequencyHz, sampleRateHz, firstSampleIndex, secondAmplitude);
        for (var index = 0; index < first.Length; index++)
        {
            first[index] += second[index];
        }

        return first;
    }

    public static ComplexF[] Blocker(
        int count,
        double wantedFrequencyHz,
        double wantedAmplitude,
        double blockerFrequencyHz,
        double blockerAmplitude,
        double sampleRateHz,
        long firstSampleIndex = 0) =>
        TwoTone(count, wantedFrequencyHz, wantedAmplitude, blockerFrequencyHz, blockerAmplitude, sampleRateHz, firstSampleIndex);

    public static ComplexF[] LinearChirp(
        int count,
        double startFrequencyHz,
        double endFrequencyHz,
        double sampleRateHz,
        double amplitude = 1,
        double phaseRadians = 0)
    {
        ValidateCommon(count, sampleRateHz, amplitude);
        ValidateFrequency(startFrequencyHz, sampleRateHz);
        ValidateFrequency(endFrequencyHz, sampleRateHz);
        if (!double.IsFinite(phaseRadians))
        {
            throw new ArgumentOutOfRangeException(nameof(phaseRadians));
        }

        var result = new ComplexF[count];
        var durationSamples = Math.Max(1, count - 1);
        var slope = (endFrequencyHz - startFrequencyHz) / durationSamples;
        for (var index = 0; index < count; index++)
        {
            var phase = phaseRadians + (2 * Math.PI / sampleRateHz *
                ((startFrequencyHz * index) + (0.5 * slope * index * index)));
            result[index] = new ComplexF((float)(amplitude * Math.Cos(phase)), (float)(amplitude * Math.Sin(phase)));
        }

        return result;
    }

    public static ComplexF[] Am(
        int count,
        double carrierFrequencyHz,
        double modulationFrequencyHz,
        double modulationIndex,
        double sampleRateHz,
        long firstSampleIndex = 0,
        double carrierAmplitude = 1)
    {
        ValidateCommon(count, sampleRateHz, carrierAmplitude);
        ValidateFrequency(carrierFrequencyHz, sampleRateHz);
        ValidateFrequency(modulationFrequencyHz, sampleRateHz);
        if (!double.IsFinite(modulationIndex) || modulationIndex < 0 || modulationIndex > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(modulationIndex));
        }

        var result = new ComplexF[count];
        for (var index = 0; index < count; index++)
        {
            var absoluteIndex = checked(firstSampleIndex + index);
            var envelope = carrierAmplitude * (1 + modulationIndex * Math.Cos(2 * Math.PI * modulationFrequencyHz * absoluteIndex / sampleRateHz));
            var phase = 2 * Math.PI * carrierFrequencyHz * absoluteIndex / sampleRateHz;
            result[index] = new ComplexF((float)(envelope * Math.Cos(phase)), (float)(envelope * Math.Sin(phase)));
        }

        return result;
    }

    public static ComplexF[] SeededNoise(int count, int seed, double rms = 1)
    {
        ValidateCommon(count, 1, rms);
        var random = new Random(seed);
        var result = new ComplexF[count];
        for (var index = 0; index < count; index++)
        {
            // Box-Muller; each component has variance rms^2 / 2.
            var radius = Math.Sqrt(-2 * Math.Log(Math.Max(double.Epsilon, random.NextDouble()))) * (rms / Math.Sqrt(2));
            var angle = 2 * Math.PI * random.NextDouble();
            result[index] = new ComplexF((float)(radius * Math.Cos(angle)), (float)(radius * Math.Sin(angle)));
        }

        return result;
    }

    public static ComplexF[] Impulse(int count, int index = 0, ComplexF? value = null)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if ((uint)index >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var result = new ComplexF[count];
        result[index] = value ?? new ComplexF(1, 0);
        return result;
    }

    public static ComplexF[] Zeros(int count) => count >= 0
        ? new ComplexF[count]
        : throw new ArgumentOutOfRangeException(nameof(count));

    private static void ValidateCommon(int count, double sampleRateHz, double amplitude)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (!double.IsFinite(sampleRateHz) || sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        if (!double.IsFinite(amplitude) || amplitude < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amplitude));
        }
    }

    private static void ValidateFrequency(double frequencyHz, double sampleRateHz)
    {
        if (!double.IsFinite(frequencyHz) || Math.Abs(frequencyHz) > sampleRateHz / 2)
        {
            throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        }
    }
}
