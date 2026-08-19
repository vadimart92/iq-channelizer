using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;
using IqChannelizer.Fftw;
using IqChannelizer.Runtime;

namespace IqChannelizer.Fdc;

internal sealed class FftwFdcEngine : StreamingEngineBase
{
    private readonly FftwComplexPlan _forwardPlan;
    private readonly InverseGroup[] _inverseGroups;
    private readonly ComplexF[][] _outputs;
    private readonly FdcChannelDesign[] _channelDesigns;
    private readonly Rotator[] _residualRotators;
    private readonly SimdPreference _simdBackend;
    private bool _residualRotatorsAnchored;

    public FftwFdcEngine(
        ResolvedChannelizerPlan plan,
        FdcChannelDesign[] channelDesigns,
        SimdPreference simdBackend,
        DiagnosticsMode diagnosticsMode)
        : base(plan, diagnosticsMode)
    {
        _channelDesigns = channelDesigns;
        _simdBackend = simdBackend;
        var transformLength = InputRequirements.InputSize;
        _forwardPlan = new FftwComplexPlan(transformLength, 1, FftwNative.Forward);
        _inverseGroups = plan.Channels
            .Select((channel, index) => (channel.DecimationFactor, ChannelIndex: index))
            .GroupBy(item => item.DecimationFactor)
            .OrderBy(group => group.Key)
            .Select(group => new InverseGroup(transformLength, InputRequirements.HistorySize, group.Key,
                group.Select(item => item.ChannelIndex).ToArray()))
            .ToArray();
        _outputs = plan.Channels.Select(channel => new ComplexF[channel.OutputSamplesPerProcess]).ToArray();
        _residualRotators = plan.Channels
            .Select(channel => Rotator.Create(
                channel.ResidualFrequencyHz,
                plan.InputSampleRateHz,
                channel.DecimationFactor,
                simdBackend == SimdPreference.Avx2 ? SimdPreference.Avx2 : SimdPreference.Scalar))
            .ToArray();
    }

    internal long ForwardTransformExecutionCount { get; private set; }
    internal int InverseGroupCount => _inverseGroups.Length;

    protected override void ProcessCore(ReadOnlySpan<ComplexF> input, long firstNewSampleIndex, IChannelOutputSink output)
    {
        var copyStartedAt = Diagnostics.BeginTiming();
        input.CopyTo(_forwardPlan.WritableInput);
        Diagnostics.RecordFdcInputCopy(
            checked(input.Length * 8),
            Diagnostics.IsTimingEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() - copyStartedAt : 0);
        var fftStartedAt = Diagnostics.BeginTiming();
        try
        {
            _forwardPlan.ExecuteFromInput();
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

        ForwardTransformExecutionCount++;
        var n = input.Length;
        var frameStartInputIndex = checked(firstNewSampleIndex - InputRequirements.HistorySize);

        foreach (var group in _inverseGroups)
        {
            var extractionStartedAt = Diagnostics.BeginTiming();
            for (var groupChannelIndex = 0; groupChannelIndex < group.ChannelIndices.Length; groupChannelIndex++)
            {
                var channelIndex = group.ChannelIndices[groupChannelIndex];
                var channel = Plan.Channels[channelIndex];
                var inverseInput = group.BackwardPlan.WritableInput.Slice(
                    groupChannelIndex * group.ShortLength,
                    group.ShortLength);
                // Local FFT mixing omits the absolute frame origin. This scalar restores it once per block;
                // the short-IFFT index then supplies the local coarse phase progression.
                var blockPhase = Rotator.CreatePhasor(
                    channel.CoarseCenterFrequencyHz,
                    Plan.InputSampleRateHz,
                    frameStartInputIndex);
                if (_simdBackend == SimdPreference.Avx512)
                {
                    SpectralSliceExtractor.ExtractAvx512Unchecked(
                        _forwardPlan.Output,
                        channel.CoarseBin,
                        _channelDesigns[channelIndex].SpectralWindow,
                        blockPhase,
                        inverseInput);
                }
                else if (_simdBackend == SimdPreference.Avx2)
                {
                    SpectralSliceExtractor.ExtractAvx2Unchecked(
                        _forwardPlan.Output,
                        channel.CoarseBin,
                        _channelDesigns[channelIndex].SpectralWindow,
                        blockPhase,
                        inverseInput);
                }
                else
                {
                    SpectralSliceExtractor.ExtractUnchecked(
                        _forwardPlan.Output,
                        channel.CoarseBin,
                        _channelDesigns[channelIndex].SpectralWindow,
                        blockPhase,
                        inverseInput);
                }
            }

            if (Diagnostics.IsTimingEnabled)
            {
                Diagnostics.RecordChannelProcessing(System.Diagnostics.Stopwatch.GetTimestamp() - extractionStartedAt);
            }

            fftStartedAt = Diagnostics.BeginTiming();
            try
            {
                group.BackwardPlan.ExecuteFromInput();
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
            EnsureResidualRotatorsAnchored(firstNewSampleIndex);
            for (var groupChannelIndex = 0; groupChannelIndex < group.ChannelIndices.Length; groupChannelIndex++)
            {
                var channelIndex = group.ChannelIndices[groupChannelIndex];
                var inverseOutput = group.BackwardPlan.Output.Slice(
                    groupChannelIndex * group.ShortLength,
                    group.ShortLength);
                var destination = _outputs[channelIndex].AsSpan();
                for (var index = 0; index < destination.Length; index++)
                {
                    destination[index] = inverseOutput[group.Discard + index] * (1f / n);
                }

                _residualRotators[channelIndex].RotateInPlace(destination);
            }

            if (Diagnostics.IsTimingEnabled)
            {
                Diagnostics.RecordChannelProcessing(System.Diagnostics.Stopwatch.GetTimestamp() - channelStartedAt);
            }
        }

        // Preserve the public request order even though inverse transforms execute by D group.
        for (var channelIndex = 0; channelIndex < Plan.Channels.Count; channelIndex++)
        {
            WriteOutput(output, channelIndex, _outputs[channelIndex]);
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

    protected override void ResetCore() => _residualRotatorsAnchored = false;

    private void EnsureResidualRotatorsAnchored(long firstNewSampleIndex)
    {
        if (_residualRotatorsAnchored)
        {
            return;
        }

        foreach (var rotator in _residualRotators)
        {
            rotator.SetPhaseFromAbsoluteIndex(firstNewSampleIndex);
        }

        _residualRotatorsAnchored = true;
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
        }

        public int Decimation { get; }
        public int[] ChannelIndices { get; }
        public int ShortLength { get; }
        public int Discard { get; }
        public FftwComplexPlan BackwardPlan { get; }
    }
}
