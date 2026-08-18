using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;

namespace IqChannelizer.Fdc;

internal sealed record FdcChannelDesign(
    float[] Taps,
    ComplexF[] SpectralWindow,
    AliasedResponseResult AliasedResponse);

internal static class FdcFilterDesign
{
    private const int DenseResponsePoints = 16_385;
    private const int FoldedResponsePoints = 1_025;

    public static float[] DesignAlignedTaps(ChannelRequest channel, double inputSampleRateHz, int decimation)
    {
        var passbandEdge = channel.PassbandWidthHz / 2;
        var stopbandEdge = (channel.PassbandWidthHz + channel.TransitionWidthHz) / 2;
        // Magnitude-summing D-1 alias images can cost up to 20*log10(D-1) dB.
        var aliasBudgetDb = 20 * Math.Log10(Math.Max(1, decimation - 1));
        var filter = KaiserLowPassDesigner.Design(new LowPassFilterSpec(
            inputSampleRateHz,
            passbandEdge,
            stopbandEdge,
            channel.PassbandRippleDb,
            channel.StopbandAttenuationDb + aliasBudgetDb));

        var alignedOrder = checked(((filter.Order + decimation - 1) / decimation) * decimation);
        if (alignedOrder == filter.Order)
        {
            return filter.Taps.ToArray();
        }

        // Symmetric zero-padding retains the magnitude response while making HistorySize/D exact.
        var leadingZeros = (alignedOrder - filter.Order) / 2;
        var taps = new float[alignedOrder + 1];
        filter.Taps.Span.CopyTo(taps.AsSpan(leadingZeros));
        return taps;
    }

    public static FdcChannelDesign Complete(
        ChannelRequest channel,
        float[] taps,
        double inputSampleRateHz,
        int decimation,
        int transformLength,
        double residualFrequencyHz)
    {
        var shortLength = transformLength / decimation;
        var window = new ComplexF[shortLength];
        for (var shortBin = 0; shortBin < shortLength; shortBin++)
        {
            var signedOffset = shortBin <= shortLength / 2 ? shortBin : shortBin - shortLength;
            var frequency = WrapFrequency(
                (signedOffset * inputSampleRateHz / transformLength) - residualFrequencyHz,
                inputSampleRateHz);
            var response = FrequencyResponseEvaluator.Evaluate(taps, frequency, inputSampleRateHz);
            window[shortBin] = new ComplexF((float)response.Real, (float)response.Imaginary);
        }

        var dense = FrequencyResponseEvaluator.EvaluateDenseConservative(taps, inputSampleRateHz, DenseResponsePoints);
        var aliased = AliasedResponseEvaluator.EvaluateConservative(
            dense,
            decimation,
            channel.PassbandWidthHz / 2,
            FoldedResponsePoints);
        if (aliased.WorstAliasAttenuationDb + 1e-6 < channel.StopbandAttenuationDb)
        {
            throw new ArgumentException(
                $"FDC filter for channel {channel.ChannelId} achieves only {aliased.WorstAliasAttenuationDb:R} dB conservative folded attenuation.");
        }

        return new FdcChannelDesign(taps, window, aliased);
    }

    private static double WrapFrequency(double frequencyHz, double sampleRateHz)
    {
        var wrapped = frequencyHz - (Math.Floor((frequencyHz + (sampleRateHz / 2)) / sampleRateHz) * sampleRateHz);
        return wrapped < -sampleRateHz / 2 ? wrapped + sampleRateHz : wrapped;
    }
}
