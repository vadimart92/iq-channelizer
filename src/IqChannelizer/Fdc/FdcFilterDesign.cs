using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;
using IqChannelizer.Fftw;

namespace IqChannelizer.Fdc;

internal sealed record FdcChannelDesign(
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
        if (taps.Length > transformLength)
        {
            throw new ArgumentException("FDC taps must fit in the overlap-save transform.", nameof(taps));
        }

        var aliased = ValidateAliasedResponse(channel, taps, inputSampleRateHz, decimation);

        // Sampling H(f) independently for every short-IFFT bin is O(taps * bins)
        // and dominates initialization for large-D filters. A single FFT of
        // h[n] * exp(+j*2*pi*residual*n/Fs) yields exactly H(f-residual) on the
        // transform grid, including the phase from engine-wide zero padding.
        using var responsePlan = new FftwComplexPlan(transformLength, 1, FftwNative.Forward);
        var responseInput = responsePlan.WritableInput;
        responseInput.Clear();
        if (residualFrequencyHz == 0)
        {
            for (var index = 0; index < taps.Length; index++)
            {
                responseInput[index] = new ComplexF(taps[index], 0);
            }
        }
        else
        {
            var radiansPerSample = 2 * Math.PI * residualFrequencyHz / inputSampleRateHz;
            for (var index = 0; index < taps.Length; index++)
            {
                var (sine, cosine) = Math.SinCos(radiansPerSample * index);
                responseInput[index] = new ComplexF(
                    (float)(taps[index] * cosine),
                    (float)(taps[index] * sine));
            }
        }

        responsePlan.ExecuteFromInput();
        var spectrum = responsePlan.Output;
        var window = new ComplexF[shortLength];
        for (var shortBin = 0; shortBin < shortLength; shortBin++)
        {
            var signedOffset = shortBin <= shortLength / 2 ? shortBin : shortBin - shortLength;
            window[shortBin] = spectrum[FrequencyBinMath.Mod(signedOffset, transformLength)];
        }

        return new FdcChannelDesign(window, aliased);
    }

    internal static AliasedResponseResult ValidateAliasedResponse(
        ChannelRequest channel,
        ReadOnlySpan<float> taps,
        double inputSampleRateHz,
        int decimation)
    {
        var dense = FrequencyResponseEvaluator.EvaluateDenseConservative(
            taps,
            inputSampleRateHz,
            DenseResponsePoints);
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

        return aliased;
    }
}
