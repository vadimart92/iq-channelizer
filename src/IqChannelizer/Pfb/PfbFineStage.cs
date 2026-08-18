using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;

namespace IqChannelizer.Pfb;

internal sealed record PfbFineStageDesign(
    int DecimationFactor,
    float[] Taps,
    RationalSampleOffset GroupDelayCoarseSamples,
    AliasedResponseResult AliasedResponse)
{
    public string FilterId => Taps.Length == 1 ? "Identity" : $"KaiserFineD{DecimationFactor}Order{Taps.Length - 1}";
}

internal static class PfbFineStageDesigner
{
    public static PfbFineStageDesign Design(ChannelRequest channel, double coarseSampleRateHz, int framesPerBatch)
    {
        var targetRate = Math.Max(
            channel.PassbandWidthHz + channel.TransitionWidthHz,
            Math.Max(channel.MinimumOutputSampleRateHz ?? 0, channel.PreferredOutputSampleRateHz ?? 0));
        var factor = 1;
        while (factor <= framesPerBatch / 2)
        {
            var candidate = factor * 2;
            if (framesPerBatch % candidate != 0 || coarseSampleRateHz / candidate < targetRate)
            {
                break;
            }

            factor = candidate;
        }

        var stopbandEdge = (channel.PassbandWidthHz + channel.TransitionWidthHz) / 2;
        if (factor == 1 && stopbandEdge >= coarseSampleRateHz / 2)
        {
            // There is no representable stopband above this edge at the coarse
            // rate. In every other D=1 case a real per-channel filter is still
            // required: the shared prototype is centered on the coarse bin and
            // can be wider or asymmetrically placed after residual rotation.
            return new PfbFineStageDesign(
                1,
                [1f],
                new RationalSampleOffset(0, 1),
                new AliasedResponseResult(0, 0, double.PositiveInfinity, 0));
        }

        var aliasBudgetDb = 20 * Math.Log10(Math.Max(1, factor - 1));
        var specification = new LowPassFilterSpec(
            coarseSampleRateHz,
            channel.PassbandWidthHz / 2,
            stopbandEdge,
            channel.PassbandRippleDb,
            channel.StopbandAttenuationDb + aliasBudgetDb);
        var filter = KaiserLowPassDesigner.Design(specification);
        var taps = filter.Taps.ToArray();
        var dense = FrequencyResponseEvaluator.EvaluateDenseConservative(taps, coarseSampleRateHz, 16_385);
        var aliased = AliasedResponseEvaluator.EvaluateConservative(
            dense,
            factor,
            channel.PassbandWidthHz / 2,
            1_025);
        if (aliased.WorstAliasAttenuationDb + 1e-6 < channel.StopbandAttenuationDb)
        {
            throw new ArgumentException(
                $"PFB fine filter for channel {channel.ChannelId} achieves only {aliased.WorstAliasAttenuationDb:R} dB folded attenuation.");
        }

        return new PfbFineStageDesign(factor, taps, filter.GroupDelayInputSamples, aliased);
    }
}

internal sealed class StreamingFineDecimator
{
    private readonly int _factor;
    private readonly float[] _taps;
    private readonly ComplexF[] _history;
    private readonly ComplexF[] _buffer;

    public StreamingFineDecimator(PfbFineStageDesign design, int inputCount)
    {
        _factor = design.DecimationFactor;
        _taps = design.Taps;
        _history = new ComplexF[_taps.Length - 1];
        _buffer = new ComplexF[checked(_history.Length + inputCount)];
    }

    public void Process(ReadOnlySpan<ComplexF> input, Span<ComplexF> output)
    {
        if (input.Length + _history.Length != _buffer.Length || output.Length != input.Length / _factor)
        {
            throw new ArgumentException("Fine-decimator block shape does not match its resolved plan.");
        }

        _history.CopyTo(_buffer, 0);
        input.CopyTo(_buffer.AsSpan(_history.Length));
        ScalarPowerOfTwoDecimator.Decimate(_buffer, _taps, _factor, phase: 0, output);
        if (_history.Length > 0)
        {
            _buffer.AsSpan(_buffer.Length - _history.Length).CopyTo(_history);
        }
    }

    public void Reset() => Array.Clear(_history);
}
