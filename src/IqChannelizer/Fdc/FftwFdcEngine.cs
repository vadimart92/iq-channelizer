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
    private readonly FdcChannelDesign[] _channelDesigns;
    private readonly int _decimation;

    public FftwFdcEngine(ResolvedChannelizerPlan plan, int decimation, FdcChannelDesign[] channelDesigns)
        : base(plan)
    {
        _decimation = decimation;
        _channelDesigns = channelDesigns;
        var transformLength = InputRequirements.InputSize;
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
        _forwardPlan.Execute(input, _spectrum);
        var n = input.Length;
        var shortLength = n / _decimation;
        var frameStartInputIndex = checked(firstNewSampleIndex - InputRequirements.HistorySize);

        for (var channelIndex = 0; channelIndex < Plan.Channels.Count; channelIndex++)
        {
            var channel = Plan.Channels[channelIndex];
            var inverseInput = _inverseInput.AsSpan(channelIndex * shortLength, shortLength);
            // Local FFT mixing omits the absolute frame origin. This scalar restores it once per block;
            // the short-IFFT index then supplies the local coarse phase progression.
            var blockPhase = ComplexF.FromPolar(
                -2 * Math.PI * channel.CoarseCenterFrequencyHz * frameStartInputIndex / Plan.InputSampleRateHz);
            SpectralSliceExtractor.Extract(
                _spectrum,
                channel.CoarseBin,
                _channelDesigns[channelIndex].SpectralWindow,
                blockPhase,
                inverseInput);
        }

        _backwardPlan.Execute(_inverseInput, _inverseOutput);
        var discard = InputRequirements.HistorySize / _decimation;
        for (var channelIndex = 0; channelIndex < Plan.Channels.Count; channelIndex++)
        {
            var channel = Plan.Channels[channelIndex];
            var inverseOutput = _inverseOutput.AsSpan(channelIndex * shortLength, shortLength);
            var destination = _outputs[channelIndex].AsSpan();
            for (var index = 0; index < destination.Length; index++)
            {
                destination[index] = inverseOutput[discard + index] * (1f / n);
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
