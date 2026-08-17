using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;
using IqChannelizer.Runtime;

namespace IqChannelizer.Pfb;

internal sealed class ScalarPfbEngine : StreamingEngineBase
{
    private readonly int _fftSize;
    private readonly int _hopSize;
    private readonly int _frames;
    private readonly float[] _prototype;
    private readonly ComplexF[] _phaseVector;
    private readonly ComplexF[] _shifted;
    private readonly ComplexF[] _bins;
    private readonly ComplexF[][] _outputs;

    public ScalarPfbEngine(ResolvedChannelizerPlan plan, int fftSize, int hopSize, int frames)
        : base(plan)
    {
        _fftSize = fftSize;
        _hopSize = hopSize;
        _frames = frames;
        _prototype = Enumerable.Repeat(1f / fftSize, fftSize).ToArray();
        _phaseVector = new ComplexF[fftSize];
        _shifted = new ComplexF[fftSize];
        _bins = new ComplexF[fftSize];
        _outputs = plan.Channels.Select(channel => new ComplexF[channel.OutputSamplesPerProcess]).ToArray();
    }

    protected override void ProcessCore(ReadOnlySpan<ComplexF> input, long firstNewSampleIndex, IChannelOutputSink output)
    {
        var spanAbsoluteStart = firstNewSampleIndex - InputRequirements.HistorySize;
        for (var frame = 0; frame < _frames; frame++)
        {
            var anchor = checked(firstNewSampleIndex + ((long)(frame + 1) * _hopSize) - 1);
            for (var phase = 0; phase < _fftSize; phase++)
            {
                var absoluteIndex = anchor - phase;
                var spanIndex = checked((int)(absoluteIndex - spanAbsoluteStart));
                _phaseVector[phase] = input[spanIndex] * _prototype[phase];
            }

            PfbMath.TransformWithCircularShift(_phaseVector, anchor, _shifted, _bins);
            for (var channelIndex = 0; channelIndex < Plan.Channels.Count; channelIndex++)
            {
                _outputs[channelIndex][frame] = _bins[Plan.Channels[channelIndex].CoarseBin];
            }
        }

        for (var channelIndex = 0; channelIndex < Plan.Channels.Count; channelIndex++)
        {
            var channel = Plan.Channels[channelIndex];
            var destination = _outputs[channelIndex].AsSpan();
            var firstAnchor = firstNewSampleIndex + _hopSize - 1;
            ScalarRotator.RotateInPlace(
                destination,
                channel.ResidualFrequencyHz,
                Plan.InputSampleRateHz,
                firstAnchor,
                _hopSize);
            output.Write(channel.ChannelId, destination);
        }
    }
}
