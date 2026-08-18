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

internal static class PfbPrototypeDesign
{
    private const int DenseResponsePoints = 16_385;
    private const int FoldedResponsePoints = 1_025;

    public static PfbPrototype Design(ChannelizerRequest request, int fftSize, int hopSize)
    {
        var passbandEdge = 0d;
        var stopbandEdge = double.PositiveInfinity;
        var attenuation = 0d;
        var ripple = double.PositiveInfinity;
        foreach (var channel in request.Channels)
        {
            var bin = PfbMath.Mod((long)Math.Round(channel.CenterFrequencyHz * fftSize / request.InputSampleRateHz), fftSize);
            var signedBin = bin <= fftSize / 2 ? bin : bin - fftSize;
            var coarse = signedBin * request.InputSampleRateHz / fftSize;
            var residual = Math.Abs(channel.CenterFrequencyHz - coarse);
            passbandEdge = Math.Max(passbandEdge, residual + (channel.PassbandWidthHz / 2));
            // A shared prototype must enter its stopband by the strictest (lowest) channel edge.
            stopbandEdge = Math.Min(stopbandEdge, residual + ((channel.PassbandWidthHz + channel.TransitionWidthHz) / 2));
            attenuation = Math.Max(attenuation, channel.StopbandAttenuationDb);
            ripple = Math.Min(ripple, channel.PassbandRippleDb);
        }

        var outputRate = request.InputSampleRateHz / hopSize;
        var aliasSafeStopLimit = outputRate - passbandEdge;
        if (stopbandEdge <= passbandEdge || passbandEdge >= outputRate / 2 ||
            stopbandEdge > aliasSafeStopLimit || stopbandEdge >= request.InputSampleRateHz / 2)
        {
            throw new ArgumentException("The forced PFB K/H plan cannot contain every requested channel in one coarse bin.");
        }

        var aliasBudgetDb = 20 * Math.Log10(Math.Max(1, hopSize - 1));
        var designed = KaiserLowPassDesigner.Design(new LowPassFilterSpec(
            request.InputSampleRateHz,
            passbandEdge,
            stopbandEdge,
            ripple,
            attenuation + aliasBudgetDb));
        var paddedLength = checked(((designed.Taps.Length + fftSize - 1) / fftSize) * fftSize);
        var padding = paddedLength - designed.Taps.Length;
        var leadingZeros = padding / 2;
        var taps = new float[paddedLength];
        designed.Taps.Span.CopyTo(taps.AsSpan(leadingZeros));
        var groupDelay = new RationalSampleOffset(checked((2L * leadingZeros) + designed.Order), 2);

        var dense = FrequencyResponseEvaluator.EvaluateDense(taps, request.InputSampleRateHz, DenseResponsePoints);
        var aliased = AliasedResponseEvaluator.EvaluateConservative(
            dense,
            hopSize,
            passbandEdge,
            FoldedResponsePoints);
        if (aliased.WorstAliasAttenuationDb + 1e-6 < attenuation)
        {
            throw new ArgumentException(
                $"PFB prototype achieves only {aliased.WorstAliasAttenuationDb:R} dB conservative folded attenuation.");
        }

        return new PfbPrototype(taps, groupDelay, aliased);
    }
}
