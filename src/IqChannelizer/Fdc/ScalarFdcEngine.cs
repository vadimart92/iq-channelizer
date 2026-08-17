using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;
using IqChannelizer.Runtime;

namespace IqChannelizer.Fdc;

internal sealed class ScalarFdcEngine : StreamingEngineBase
{
    private readonly ComplexF[] _spectrum;
    private readonly ComplexF[] _slice;
    private readonly ComplexF[] _ifft;
    private readonly ComplexF[][] _outputs;
    private readonly int _decimation;

    public ScalarFdcEngine(ResolvedChannelizerPlan plan, int decimation)
        : base(plan)
    {
        _decimation = decimation;
        _spectrum = new ComplexF[InputRequirements.ChunkSize];
        _slice = new ComplexF[InputRequirements.ChunkSize / decimation];
        _ifft = new ComplexF[_slice.Length];
        _outputs = plan.Channels.Select(channel => new ComplexF[channel.OutputSamplesPerProcess]).ToArray();
    }

    protected override void ProcessCore(ReadOnlySpan<ComplexF> input, long firstNewSampleIndex, IChannelOutputSink output)
    {
        var chunk = input[InputRequirements.HistorySize..];
        ScalarDft.Forward(chunk, _spectrum);
        var n = chunk.Length;
        var shortLength = _slice.Length;

        for (var channelIndex = 0; channelIndex < Plan.Channels.Count; channelIndex++)
        {
            var channel = Plan.Channels[channelIndex];
            for (var shortBin = 0; shortBin < shortLength; shortBin++)
            {
                var signedOffset = shortBin <= shortLength / 2 ? shortBin : shortBin - shortLength;
                var sourceBin = Pfb.PfbMath.Mod(channel.CoarseBin + signedOffset, n);
                _slice[shortBin] = _spectrum[sourceBin];
            }

            ScalarDft.Backward(_slice, _ifft);
            var destination = _outputs[channelIndex].AsSpan();
            var blockPhase = ComplexF.FromPolar(-2 * Math.PI * channel.CoarseCenterFrequencyHz * firstNewSampleIndex / Plan.InputSampleRateHz);
            for (var index = 0; index < destination.Length; index++)
            {
                destination[index] = (_ifft[index] * (1f / n)) * blockPhase;
            }

            ScalarRotator.RotateInPlace(
                destination,
                channel.ResidualFrequencyHz,
                Plan.InputSampleRateHz,
                firstNewSampleIndex,
                _decimation);
            output.Write(channel.ChannelId, destination);
        }
    }
}
