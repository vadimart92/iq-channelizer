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
    private readonly Avx2PfbCoefficients? _avx2Coefficients;
    private readonly Avx512PfbCoefficients? _avx512Coefficients;
    private readonly FftwComplexPlan _backwardPlan;
    private readonly int[] _uniqueBins;
    private readonly int[] _channelRoutes;
    private readonly ComplexF[][] _coarseStreams;
    private readonly ComplexF[][] _rotatedStreams;
    private readonly Rotator[] _residualRotators;
    private readonly StreamingFineDecimator[] _fineDecimators;
    private readonly ComplexF[][] _outputs;
    private bool _residualRotatorsAnchored;

    public FftwPfbEngine(
        ResolvedChannelizerPlan plan,
        int fftSize,
        int hopSize,
        int frames,
        float[] prototype,
        PfbFineStageDesign[] fineStageDesigns,
        SimdPreference simdBackend,
        DiagnosticsMode diagnosticsMode)
        : base(plan, diagnosticsMode)
    {
        _fftSize = fftSize;
        _hopSize = hopSize;
        _frames = frames;
        _prototype = prototype;
        _avx2Coefficients = simdBackend == SimdPreference.Avx2
            ? new Avx2PfbCoefficients(prototype, fftSize)
            : null;
        _avx512Coefficients = simdBackend == SimdPreference.Avx512
            ? new Avx512PfbCoefficients(prototype, fftSize)
            : null;
        _backwardPlan = new FftwComplexPlan(fftSize, frames, FftwNative.Backward);
        _uniqueBins = plan.Channels.Select(channel => channel.CoarseBin).Distinct().ToArray();
        _channelRoutes = plan.Channels.Select(channel => Array.IndexOf(_uniqueBins, channel.CoarseBin)).ToArray();
        _coarseStreams = _uniqueBins.Select(_ => new ComplexF[frames]).ToArray();
        _rotatedStreams = plan.Channels
            .Select(channel => channel.ResidualFrequencyHz == 0 ? [] : new ComplexF[frames])
            .ToArray();
        _residualRotators = plan.Channels
            .Select(channel => new Rotator(
                channel.ResidualFrequencyHz,
                plan.InputSampleRateHz,
                hopSize,
                simdBackend == SimdPreference.Avx2 ? SimdPreference.Avx2 : SimdPreference.Scalar))
            .ToArray();
        _fineDecimators = fineStageDesigns.Select(design => new StreamingFineDecimator(design, frames)).ToArray();
        _outputs = plan.Channels.Select(channel => new ComplexF[channel.OutputSamplesPerProcess]).ToArray();
        RotationChannelCount = plan.Channels.Count(channel => channel.ResidualFrequencyHz != 0);
        PrototypeOnlyChannelCount = fineStageDesigns.Count(
            design => design.DecimationFactor == 1 && design.Taps.Length == 1 && design.Taps[0] == 1f);
    }

    internal int UniqueBinCount => _uniqueBins.Length;
    internal int RotationChannelCount { get; }
    internal int PrototypeOnlyChannelCount { get; }
    internal long GatheredBinValueCount { get; private set; }

    protected override void ProcessCore(ReadOnlySpan<ComplexF> input, long firstNewSampleIndex, IChannelOutputSink output)
    {
        var spanAbsoluteStart = firstNewSampleIndex - InputRequirements.HistorySize;
        var polyphaseStartedAt = Diagnostics.BeginTiming();
        if (_avx512Coefficients is { } avx512Coefficients)
        {
            PfbPhaseFir.FillBatchAvx512(
                input,
                spanAbsoluteStart,
                firstNewSampleIndex,
                _hopSize,
                _frames,
                _prototype,
                avx512Coefficients,
                _backwardPlan.WritableInput);
        }
        else if (_avx2Coefficients is { } coefficients)
        {
            PfbPhaseFir.FillBatchAvx2(
                input,
                spanAbsoluteStart,
                firstNewSampleIndex,
                _hopSize,
                _frames,
                _prototype,
                coefficients,
                _backwardPlan.WritableInput);
        }
        else
        {
            PfbPhaseFir.FillBatchScalar(
                input,
                spanAbsoluteStart,
                firstNewSampleIndex,
                _hopSize,
                _frames,
                _fftSize,
                _prototype,
                _backwardPlan.WritableInput);
        }

        Diagnostics.RecordPfbPolyphase(
            InputRequirements.ChunkSize,
            Diagnostics.IsTimingEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() - polyphaseStartedAt : 0);

        var fftStartedAt = Diagnostics.BeginTiming();
        try
        {
            _backwardPlan.ExecuteFromInput();
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
            var bins = _backwardPlan.Output.Slice(frame * _fftSize, _fftSize);
            for (var uniqueIndex = 0; uniqueIndex < _uniqueBins.Length; uniqueIndex++)
            {
                _coarseStreams[uniqueIndex][frame] = bins[_uniqueBins[uniqueIndex]];
                GatheredBinValueCount++;
            }
        }

        for (var channelIndex = 0; channelIndex < Plan.Channels.Count; channelIndex++)
        {
            var channel = Plan.Channels[channelIndex];
            ReadOnlySpan<ComplexF> fineInput = _coarseStreams[_channelRoutes[channelIndex]];
            if (channel.ResidualFrequencyHz != 0)
            {
                var rotated = _rotatedStreams[channelIndex].AsSpan();
                fineInput.CopyTo(rotated);
                var firstAnchor = firstNewSampleIndex + _hopSize - 1;
                EnsureResidualRotatorsAnchored(firstAnchor);
                _residualRotators[channelIndex].RotateInPlace(rotated);
                fineInput = rotated;
            }

            var destination = _outputs[channelIndex].AsSpan();
            _fineDecimators[channelIndex].Process(fineInput, destination);
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

        _residualRotatorsAnchored = false;
    }

    private void EnsureResidualRotatorsAnchored(long firstAnchor)
    {
        if (_residualRotatorsAnchored)
        {
            return;
        }

        foreach (var rotator in _residualRotators)
        {
            rotator.SetPhaseFromAbsoluteIndex(firstAnchor);
        }

        _residualRotatorsAnchored = true;
    }

}
