using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;
using IqChannelizer.Fftw;
using IqChannelizer.Runtime;

namespace IqChannelizer.Fdc;

internal sealed class FftwFdcEngine : StreamingEngineBase
{
    private readonly FftwComplexPlan _forwardPlan;
    private readonly FftwComplexPlan _backwardPlan;
    private readonly ComplexF[] _spectrum;
    private readonly ComplexF[] _inverseInput;
    private readonly ComplexF[] _inverseOutput;
    private readonly ComplexF[][] _outputs;
    private readonly int _decimation;

    public FftwFdcEngine(ResolvedChannelizerPlan plan, int decimation)
        : base(plan)
    {
        _decimation = decimation;
        var transformLength = InputRequirements.ChunkSize;
        var shortLength = transformLength / decimation;
        _forwardPlan = new FftwComplexPlan(transformLength, 1, FftwNative.Forward);
        _backwardPlan = new FftwComplexPlan(shortLength, plan.Channels.Count, FftwNative.Backward);
        _spectrum = new ComplexF[transformLength];
        _inverseInput = new ComplexF[checked(shortLength * plan.Channels.Count)];
        _inverseOutput = new ComplexF[_inverseInput.Length];
        _outputs = plan.Channels.Select(channel => new ComplexF[channel.OutputSamplesPerProcess]).ToArray();
    }

    protected override void ProcessCore(ReadOnlySpan<ComplexF> input, long firstNewSampleIndex, IChannelOutputSink output)
    {
        var chunk = input[InputRequirements.HistorySize..];
        _forwardPlan.Execute(chunk, _spectrum);
        var n = chunk.Length;
        var shortLength = n / _decimation;

        for (var channelIndex = 0; channelIndex < Plan.Channels.Count; channelIndex++)
        {
            var channel = Plan.Channels[channelIndex];
            var inverseInput = _inverseInput.AsSpan(channelIndex * shortLength, shortLength);
            for (var shortBin = 0; shortBin < shortLength; shortBin++)
            {
                var signedOffset = shortBin <= shortLength / 2 ? shortBin : shortBin - shortLength;
                var sourceBin = Pfb.PfbMath.Mod(channel.CoarseBin + signedOffset, n);
                inverseInput[shortBin] = _spectrum[sourceBin];
            }
        }

        _backwardPlan.Execute(_inverseInput, _inverseOutput);
        for (var channelIndex = 0; channelIndex < Plan.Channels.Count; channelIndex++)
        {
            var channel = Plan.Channels[channelIndex];
            var inverseOutput = _inverseOutput.AsSpan(channelIndex * shortLength, shortLength);
            var destination = _outputs[channelIndex].AsSpan();
            var blockPhase = ComplexF.FromPolar(-2 * Math.PI * channel.CoarseCenterFrequencyHz * firstNewSampleIndex / Plan.InputSampleRateHz);
            for (var index = 0; index < destination.Length; index++)
            {
                destination[index] = (inverseOutput[index] * (1f / n)) * blockPhase;
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

    protected override void DisposeCore()
    {
        _backwardPlan.Dispose();
        _forwardPlan.Dispose();
    }
}
