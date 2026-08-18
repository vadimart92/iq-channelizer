namespace IqChannelizer.Dsp;

internal static class FrequencyBinMath
{
    public static int NearestNormalizedBin(double frequencyHz, double sampleRateHz, int transformLength)
    {
        if (!double.IsFinite(frequencyHz) || !double.IsFinite(sampleRateHz) || sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        }

        if (transformLength < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(transformLength));
        }

        var rounded = (long)Math.Round(frequencyHz * transformLength / sampleRateHz);
        return Mod(rounded, transformLength);
    }

    public static int ToSignedBin(int normalizedBin, int transformLength)
    {
        if (transformLength < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(transformLength));
        }

        var normalized = Mod(normalizedBin, transformLength);
        // An even transform has one ambiguous Nyquist bin. The public frequency
        // interval is [-Fs/2, Fs/2), so its canonical representation is -N/2.
        var maximumPositiveBin = (transformLength - 1) / 2;
        return normalized <= maximumPositiveBin ? normalized : normalized - transformLength;
    }

    public static double WrappedDifference(double frequencyHz, double referenceFrequencyHz, double sampleRateHz)
    {
        if (!double.IsFinite(frequencyHz) || !double.IsFinite(referenceFrequencyHz) ||
            !double.IsFinite(sampleRateHz) || sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        }

        var difference = (frequencyHz - referenceFrequencyHz) % sampleRateHz;
        if (difference >= sampleRateHz / 2)
        {
            difference -= sampleRateHz;
        }
        else if (difference < -sampleRateHz / 2)
        {
            difference += sampleRateHz;
        }

        return difference == 0 ? 0 : difference;
    }

    public static int Mod(long value, int modulus)
    {
        if (modulus <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(modulus));
        }

        var result = (int)(value % modulus);
        return result < 0 ? result + modulus : result;
    }
}
