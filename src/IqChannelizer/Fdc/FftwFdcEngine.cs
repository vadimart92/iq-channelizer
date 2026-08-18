using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;
using IqChannelizer.Fftw;
using IqChannelizer.Runtime;

namespace IqChannelizer.Fdc;

internal sealed class FftwFdcEngine : StreamingEngineBase
{
    private readonly FftwComplexPlan _forwardPlan;
    private readonly ComplexF[] _spectrum;
    private readonly InverseGroup[] _inverseGroups;
    private readonly ComplexF[][] _outputs;
    private readonly FdcChannelDesign[] _channelDesigns;

    public FftwFdcEngine(ResolvedChannelizerPlan plan, FdcChannelDesign[] channelDesigns)
        : base(plan)
    {
        _channelDesigns = channelDesigns;
        var transformLength = InputRequirements.InputSize;
        _forwardPlan = new FftwComplexPlan(transformLength, 1, FftwNative.Forward);
        _spectrum = new ComplexF[transformLength];
        _inverseGroups = plan.Channels
            .Select((channel, index) => (channel.DecimationFactor, ChannelIndex: index))
            .GroupBy(item => item.DecimationFactor)
            .OrderBy(group => group.Key)
            .Select(group => new InverseGroup(transformLength, InputRequirements.HistorySize, group.Key,
                group.Select(item => item.ChannelIndex).ToArray()))
            .ToArray();
        _outputs = plan.Channels.Select(channel => new ComplexF[channel.OutputSamplesPerProcess]).ToArray();
    }

    internal long ForwardTransformExecutionCount { get; private set; }
    internal int InverseGroupCount => _inverseGroups.Length;

    protected override void ProcessCore(ReadOnlySpan<ComplexF> input, long firstNewSampleIndex, IChannelOutputSink output)
    {
        _forwardPlan.Execute(input, _spectrum);
        ForwardTransformExecutionCount++;
        var n = input.Length;
        var frameStartInputIndex = checked(firstNewSampleIndex - InputRequirements.HistorySize);

        foreach (var group in _inverseGroups)
        {
            for (var groupChannelIndex = 0; groupChannelIndex < group.ChannelIndices.Length; groupChannelIndex++)
            {
                var channelIndex = group.ChannelIndices[groupChannelIndex];
                var channel = Plan.Channels[channelIndex];
                var inverseInput = group.Input.AsSpan(groupChannelIndex * group.ShortLength, group.ShortLength);
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

            group.BackwardPlan.Execute(group.Input, group.Output);
            for (var groupChannelIndex = 0; groupChannelIndex < group.ChannelIndices.Length; groupChannelIndex++)
            {
                var channelIndex = group.ChannelIndices[groupChannelIndex];
                var channel = Plan.Channels[channelIndex];
                var inverseOutput = group.Output.AsSpan(groupChannelIndex * group.ShortLength, group.ShortLength);
                var destination = _outputs[channelIndex].AsSpan();
                for (var index = 0; index < destination.Length; index++)
                {
                    destination[index] = inverseOutput[group.Discard + index] * (1f / n);
                }

                ScalarRotator.RotateInPlace(
                    destination,
                    channel.ResidualFrequencyHz,
                    Plan.InputSampleRateHz,
                    firstNewSampleIndex,
                    group.Decimation);
            }
        }

        // Preserve the public request order even though inverse transforms execute by D group.
        for (var channelIndex = 0; channelIndex < Plan.Channels.Count; channelIndex++)
        {
            output.Write(Plan.Channels[channelIndex].ChannelId, _outputs[channelIndex]);
        }
    }

    protected override void DisposeCore()
    {
        foreach (var group in _inverseGroups)
        {
            group.BackwardPlan.Dispose();
        }

        _forwardPlan.Dispose();
    }

    private sealed class InverseGroup
    {
        public InverseGroup(int transformLength, int historySize, int decimation, int[] channelIndices)
        {
            Decimation = decimation;
            ChannelIndices = channelIndices;
            ShortLength = transformLength / decimation;
            Discard = historySize / decimation;
            BackwardPlan = new FftwComplexPlan(ShortLength, channelIndices.Length, FftwNative.Backward);
            Input = new ComplexF[checked(ShortLength * channelIndices.Length)];
            Output = new ComplexF[Input.Length];
        }

        public int Decimation { get; }
        public int[] ChannelIndices { get; }
        public int ShortLength { get; }
        public int Discard { get; }
        public FftwComplexPlan BackwardPlan { get; }
        public ComplexF[] Input { get; }
        public ComplexF[] Output { get; }
    }
}
