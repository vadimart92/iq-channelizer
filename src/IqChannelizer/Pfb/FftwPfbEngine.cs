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
    private readonly ComplexF[] _fftOutput;
    private readonly int[] _uniqueBins;
    private readonly int[] _channelRoutes;
    private readonly ComplexF[][] _coarseStreams;
    private readonly ComplexF[][] _rotatedStreams;
    private readonly StreamingFineDecimator[] _fineDecimators;
    private readonly ComplexF[][] _outputs;

    public FftwPfbEngine(
        ResolvedChannelizerPlan plan,
        int fftSize,
        int hopSize,
        int frames,
        float[] prototype,
        PfbFineStageDesign[] fineStageDesigns,
        DiagnosticsMode diagnosticsMode)
        : base(plan, diagnosticsMode)
    {
        _fftSize = fftSize;
        _hopSize = hopSize;
        _frames = frames;
        _prototype = prototype;
        _backwardPlan = new FftwComplexPlan(fftSize, frames, FftwNative.Backward);
        _fftOutput = new ComplexF[checked(fftSize * frames)];
        _uniqueBins = plan.Channels.Select(channel => channel.CoarseBin).Distinct().ToArray();
        _channelRoutes = plan.Channels.Select(channel => Array.IndexOf(_uniqueBins, channel.CoarseBin)).ToArray();
        _coarseStreams = _uniqueBins.Select(_ => new ComplexF[frames]).ToArray();
        _rotatedStreams = plan.Channels.Select(_ => new ComplexF[frames]).ToArray();
        _fineDecimators = fineStageDesigns.Select(design => new StreamingFineDecimator(design, frames)).ToArray();
        _outputs = plan.Channels.Select(channel => new ComplexF[channel.OutputSamplesPerProcess]).ToArray();
    }

    internal int UniqueBinCount => _uniqueBins.Length;
    internal long GatheredBinValueCount { get; private set; }

    protected override void ProcessCore(ReadOnlySpan<ComplexF> input, long firstNewSampleIndex, IChannelOutputSink output)
    {
        var spanAbsoluteStart = firstNewSampleIndex - InputRequirements.HistorySize;
        var polyphaseStartedAt = Diagnostics.BeginTiming();
        for (var frame = 0; frame < _frames; frame++)
        {
            var anchor = checked(firstNewSampleIndex + ((long)(frame + 1) * _hopSize) - 1);
            var shift = PfbMath.Mod(anchor, _fftSize);
            var fftInput = _backwardPlan.WritableInput.Slice(frame * _fftSize, _fftSize);
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

        Diagnostics.RecordPfbPolyphase(
            InputRequirements.ChunkSize,
            Diagnostics.IsTimingEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() - polyphaseStartedAt : 0);

        var fftStartedAt = Diagnostics.BeginTiming();
        try
        {
            _backwardPlan.ExecuteFromInput(_fftOutput);
        }
        catch
        {
            Diagnostics.RecordFftwFailure();
            throw;
        }
        finally
        {
            if (Diagnostics.IsTimingEnabled)
            {
                Diagnostics.RecordFftwExecution(System.Diagnostics.Stopwatch.GetTimestamp() - fftStartedAt);
            }
        }

        var channelStartedAt = Diagnostics.BeginTiming();
        for (var frame = 0; frame < _frames; frame++)
        {
            var bins = _fftOutput.AsSpan(frame * _fftSize, _fftSize);
            for (var uniqueIndex = 0; uniqueIndex < _uniqueBins.Length; uniqueIndex++)
            {
                _coarseStreams[uniqueIndex][frame] = bins[_uniqueBins[uniqueIndex]];
                GatheredBinValueCount++;
            }
        }

        for (var channelIndex = 0; channelIndex < Plan.Channels.Count; channelIndex++)
        {
            var channel = Plan.Channels[channelIndex];
            var rotated = _rotatedStreams[channelIndex].AsSpan();
            _coarseStreams[_channelRoutes[channelIndex]].CopyTo(rotated);
            var firstAnchor = firstNewSampleIndex + _hopSize - 1;
            ScalarRotator.RotateInPlace(
                rotated,
                channel.ResidualFrequencyHz,
                Plan.InputSampleRateHz,
                firstAnchor,
                _hopSize);
            var destination = _outputs[channelIndex].AsSpan();
            _fineDecimators[channelIndex].Process(rotated, destination);
        }

        if (Diagnostics.IsTimingEnabled)
        {
            Diagnostics.RecordChannelProcessing(System.Diagnostics.Stopwatch.GetTimestamp() - channelStartedAt);
        }

        // Advance every channel's state before exposing any output. If a sink
        // throws, StreamingEngineBase faults the engine until Reset so partially
        // emitted blocks can never be followed by silently divergent DSP state.
        for (var channelIndex = 0; channelIndex < Plan.Channels.Count; channelIndex++)
        {
            WriteOutput(output, channelIndex, _outputs[channelIndex]);
        }
    }

    protected override void DisposeCore() => _backwardPlan.Dispose();

    protected override void ResetCore()
    {
        foreach (var decimator in _fineDecimators)
        {
            decimator.Reset();
        }
    }

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
