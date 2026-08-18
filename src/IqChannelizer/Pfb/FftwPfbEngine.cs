using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;
using IqChannelizer.Fftw;
using IqChannelizer.Runtime;

namespace IqChannelizer.Pfb;

internal sealed class FftwPfbEngine : StreamingEngineBase
{
    private readonly int _fftSize;
    private readonly int _hopSize;
    private readonly int _frames;
    private readonly float[] _prototype;
    private readonly FftwComplexPlan _backwardPlan;
    private readonly ComplexF[] _fftInput;
    private readonly ComplexF[] _fftOutput;
    private readonly ComplexF[][] _outputs;

    public FftwPfbEngine(ResolvedChannelizerPlan plan, int fftSize, int hopSize, int frames, float[] prototype)
        : base(plan)
    {
        _fftSize = fftSize;
        _hopSize = hopSize;
        _frames = frames;
        _prototype = prototype;
        _backwardPlan = new FftwComplexPlan(fftSize, frames, FftwNative.Backward);
        _fftInput = new ComplexF[checked(fftSize * frames)];
        _fftOutput = new ComplexF[_fftInput.Length];
        _outputs = plan.Channels.Select(channel => new ComplexF[channel.OutputSamplesPerProcess]).ToArray();
    }

    protected override void ProcessCore(ReadOnlySpan<ComplexF> input, long firstNewSampleIndex, IChannelOutputSink output)
    {
        var spanAbsoluteStart = firstNewSampleIndex - InputRequirements.HistorySize;
        for (var frame = 0; frame < _frames; frame++)
        {
            var anchor = checked(firstNewSampleIndex + ((long)(frame + 1) * _hopSize) - 1);
            var shift = PfbMath.Mod(anchor, _fftSize);
            var fftInput = _fftInput.AsSpan(frame * _fftSize, _fftSize);
            var firstSegmentLength = _fftSize - shift;
            for (var destinationPhase = 0; destinationPhase < firstSegmentLength; destinationPhase++)
            {
                var phase = destinationPhase + shift;
                fftInput[destinationPhase] = FilterPhase(input, spanAbsoluteStart, anchor, phase);
            }

            for (var destinationPhase = firstSegmentLength; destinationPhase < _fftSize; destinationPhase++)
            {
                var phase = destinationPhase - firstSegmentLength;
                fftInput[destinationPhase] = FilterPhase(input, spanAbsoluteStart, anchor, phase);
            }
        }

        _backwardPlan.Execute(_fftInput, _fftOutput);
        for (var frame = 0; frame < _frames; frame++)
        {
            var bins = _fftOutput.AsSpan(frame * _fftSize, _fftSize);
            for (var channelIndex = 0; channelIndex < Plan.Channels.Count; channelIndex++)
            {
                _outputs[channelIndex][frame] = bins[Plan.Channels[channelIndex].CoarseBin];
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

    protected override void DisposeCore() => _backwardPlan.Dispose();

    private ComplexF FilterPhase(ReadOnlySpan<ComplexF> input, long spanAbsoluteStart, long anchor, int phase)
    {
        var accumulator = new ComplexF();
        for (var tap = phase; tap < _prototype.Length; tap += _fftSize)
        {
            var absoluteIndex = anchor - tap;
            var spanIndex = checked((int)(absoluteIndex - spanAbsoluteStart));
            accumulator += input[spanIndex] * _prototype[tap];
        }

        return accumulator;
    }
}
