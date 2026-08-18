using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;

namespace IqChannelizer.Pfb;

internal sealed record PfbPrototype(
    float[] Taps,
    RationalSampleOffset GroupDelayInputSamples,
    AliasedResponseResult AliasedResponse)
{
    public int TapsPerPhase(int fftSize) => Taps.Length / fftSize;
}

internal sealed record PfbPrototypeRequirements(
    double PassbandEdgeHz,
    double StopbandEdgeHz,
    double StopbandAttenuationDb,
    double PassbandRippleDb,
    double CoarseOutputSampleRateHz);

internal static class PfbPrototypeDesign
{
    private const int DenseResponsePoints = 16_385;
    private const int FoldedResponsePoints = 1_025;

    public static PfbPrototype Design(ChannelizerRequest request, int fftSize, int hopSize)
    {
        var requirements = Analyze(request, fftSize, hopSize);
        var aliasBudgetDb = 20 * Math.Log10(Math.Max(1, hopSize - 1));
        var designed = KaiserLowPassDesigner.Design(new LowPassFilterSpec(
            request.InputSampleRateHz,
            requirements.PassbandEdgeHz,
            requirements.StopbandEdgeHz,
            requirements.PassbandRippleDb,
            requirements.StopbandAttenuationDb + aliasBudgetDb));
        var paddedLength = checked(((designed.Taps.Length + fftSize - 1) / fftSize) * fftSize);
        var padding = paddedLength - designed.Taps.Length;
        var leadingZeros = padding / 2;
        var taps = new float[paddedLength];
        designed.Taps.Span.CopyTo(taps.AsSpan(leadingZeros));
        var groupDelay = new RationalSampleOffset(checked((2L * leadingZeros) + designed.Order), 2);

        var dense = FrequencyResponseEvaluator.EvaluateDenseConservative(taps, request.InputSampleRateHz, DenseResponsePoints);
        var aliased = AliasedResponseEvaluator.EvaluateConservative(
            dense,
            hopSize,
            requirements.PassbandEdgeHz,
            FoldedResponsePoints);
        if (aliased.WorstAliasAttenuationDb + 1e-6 < requirements.StopbandAttenuationDb)
        {
            throw new ArgumentException(
                $"PFB prototype achieves only {aliased.WorstAliasAttenuationDb:R} dB conservative folded attenuation.");
        }

        return new PfbPrototype(taps, groupDelay, aliased);
    }

    public static PfbPrototypeRequirements Analyze(ChannelizerRequest request, int fftSize, int hopSize)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (fftSize < 2 || hopSize < 1 || hopSize > fftSize)
        {
            throw new ArgumentOutOfRangeException(nameof(fftSize), "PFB requires K >= 2 and 1 <= H <= K.");
        }

        var passbandEdge = 0d;
        var stopbandEdge = 0d;
        var attenuation = 0d;
        var ripple = double.PositiveInfinity;
        foreach (var channel in request.Channels)
        {
            var bin = FrequencyBinMath.NearestNormalizedBin(
                channel.CenterFrequencyHz,
                request.InputSampleRateHz,
                fftSize);
            var signedBin = FrequencyBinMath.ToSignedBin(bin, fftSize);
            var coarse = signedBin * request.InputSampleRateHz / fftSize;
            var residual = Math.Abs(FrequencyBinMath.WrappedDifference(
                channel.CenterFrequencyHz,
                coarse,
                request.InputSampleRateHz));
            passbandEdge = Math.Max(passbandEdge, residual + (channel.PassbandWidthHz / 2));
            // The common analysis prototype admits the widest requested coarse-bin region;
            // per-channel fine filters enforce narrower individual stop edges after fan-out.
            stopbandEdge = Math.Max(stopbandEdge, residual + ((channel.PassbandWidthHz + channel.TransitionWidthHz) / 2));
            attenuation = Math.Max(attenuation, channel.StopbandAttenuationDb);
            ripple = Math.Min(ripple, channel.PassbandRippleDb);
        }

        var outputRate = request.InputSampleRateHz / hopSize;
        foreach (var channel in request.Channels)
        {
            var requiredOutputRate = Math.Max(
                channel.PassbandWidthHz + channel.TransitionWidthHz,
                channel.MinimumOutputSampleRateHz ?? 0);
            if (outputRate < requiredOutputRate)
            {
                throw new ArgumentException(
                    $"The forced PFB K/H plan produces only {outputRate:R} Hz for channel {channel.ChannelId}, " +
                    $"below its required {requiredOutputRate:R} Hz.");
            }
        }

        var aliasSafeStopLimit = outputRate - passbandEdge;
        if (stopbandEdge <= passbandEdge || passbandEdge >= outputRate / 2 ||
            stopbandEdge > aliasSafeStopLimit || stopbandEdge >= request.InputSampleRateHz / 2)
        {
            throw new ArgumentException("The forced PFB K/H plan cannot contain every requested channel in one coarse bin.");
        }

        return new PfbPrototypeRequirements(passbandEdge, stopbandEdge, attenuation, ripple, outputRate);
    }
}
