using IqChannelizer.Abstractions;
using IqChannelizer.Dsp;
using IqChannelizer.Fftw;
using IqChannelizer.Runtime;

namespace IqChannelizer.Fdc;

/// <summary>
/// Uniform partitioned overlap-save FDC. The public block history initializes the
/// frequency-domain delay line; continuous calls then transform only the newest
/// two-partition input window.
/// </summary>
internal sealed class PartitionedFftwFdcEngine : StreamingEngineBase
{
    private readonly FftwComplexPlan _forwardPlan;
    private readonly ComplexF[][] _spectrumRing;
    private readonly InverseGroup[] _inverseGroups;
    private readonly ComplexF[][] _outputs;
    private readonly PartitionedFdcChannelDesign[] _channelDesigns;
    private readonly Rotator[] _residualRotators;
    private readonly SimdPreference _simdBackend;
    private readonly int _partitionLength;
    private readonly int _transformLength;
    private int _ringHead;
    private bool _initialized;
    private bool _residualRotatorsAnchored;

    public PartitionedFftwFdcEngine(
        ResolvedChannelizerPlan plan,
        PartitionedFdcChannelDesign[] channelDesigns,
        SimdPreference simdBackend,
        DiagnosticsMode diagnosticsMode)
        : base(plan, diagnosticsMode)
    {
        ArgumentNullException.ThrowIfNull(channelDesigns);
        if (channelDesigns.Length != plan.Channels.Count || channelDesigns.Length == 0)
        {
            throw new ArgumentException("Every FDC channel must have one partitioned design.", nameof(channelDesigns));
        }

        _partitionLength = InputRequirements.ChunkSize;
        _transformLength = checked(2 * _partitionLength);
        _channelDesigns = channelDesigns;
        _simdBackend = simdBackend;
        var partitionCount = channelDesigns[0].PartitionSpectralWindows.Length;
        if (partitionCount == 0 || channelDesigns.Any(design =>
                design.PartitionSpectralWindows.Length != partitionCount))
        {
            throw new ArgumentException("Partitioned FDC designs must share a non-empty partition count.", nameof(channelDesigns));
        }

        _forwardPlan = new FftwComplexPlan(_transformLength, 1, FftwNative.Forward);
        _spectrumRing = new ComplexF[partitionCount][];
        for (var index = 0; index < _spectrumRing.Length; index++)
        {
            _spectrumRing[index] = new ComplexF[_transformLength];
        }

        _inverseGroups = plan.Channels
            .Select((channel, index) => (channel.DecimationFactor, ChannelIndex: index))
            .GroupBy(item => item.DecimationFactor)
            .OrderBy(group => group.Key)
            .Select(group => new InverseGroup(
                _transformLength,
                _partitionLength,
                group.Key,
                group.Select(item => item.ChannelIndex).ToArray()))
            .ToArray();
        _outputs = plan.Channels
            .Select(channel => new ComplexF[channel.OutputSamplesPerProcess])
            .ToArray();
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
    internal int PartitionCount => _spectrumRing.Length;

    protected override void ProcessCore(
        ReadOnlySpan<ComplexF> input,
        long firstNewSampleIndex,
        IChannelOutputSink output)
    {
        UpdateSpectrumRing(input);
        var spectrumWindowStart = checked(firstNewSampleIndex - _partitionLength);

        foreach (var group in _inverseGroups)
        {
            var extractionStartedAt = Diagnostics.BeginTiming();
            for (var groupChannelIndex = 0;
                 groupChannelIndex < group.ChannelIndices.Length;
                 groupChannelIndex++)
            {
                var channelIndex = group.ChannelIndices[groupChannelIndex];
                var channel = Plan.Channels[channelIndex];
                var design = _channelDesigns[channelIndex];
                var inverseInput = group.BackwardPlan.WritableInput.Slice(
                    groupChannelIndex * group.ShortLength,
                    group.ShortLength);

                inverseInput.Clear();
                for (var partitionIndex = 0;
                     partitionIndex < design.PartitionSpectralWindows.Length;
                     partitionIndex++)
                {
                    var partitionWindow = design.PartitionSpectralWindows[partitionIndex];
                    for (var alias = 0; alias < group.Decimation; alias++)
                    {
                        PartitionedSpectralAccumulator.AccumulateUnchecked(
                            SpectrumAtAge(partitionIndex),
                            channel.CoarseBin + (alias * group.ShortLength),
                            partitionWindow.AsSpan(alias * group.ShortLength, group.ShortLength),
                            inverseInput,
                            _simdBackend);
                    }
                }

                var blockPhase = Rotator.CreatePhasor(
                    channel.CoarseCenterFrequencyHz,
                    Plan.InputSampleRateHz,
                    spectrumWindowStart);
                PartitionedSpectralAccumulator.ApplyPhaseUnchecked(
                    inverseInput,
                    blockPhase,
                    _simdBackend);
            }

            if (Diagnostics.IsTimingEnabled)
            {
                Diagnostics.RecordChannelProcessing(
                    System.Diagnostics.Stopwatch.GetTimestamp() - extractionStartedAt);
            }

            ExecuteInverse(group.BackwardPlan);

            var channelStartedAt = Diagnostics.BeginTiming();
            EnsureResidualRotatorsAnchored(firstNewSampleIndex);
            for (var groupChannelIndex = 0;
                 groupChannelIndex < group.ChannelIndices.Length;
                 groupChannelIndex++)
            {
                var channelIndex = group.ChannelIndices[groupChannelIndex];
                var channel = Plan.Channels[channelIndex];
                var inverseOutput = group.BackwardPlan.Output.Slice(
                    groupChannelIndex * group.ShortLength,
                    group.ShortLength);
                var destination = _outputs[channelIndex].AsSpan();
                var scale = 1f / _transformLength;
                for (var index = 0; index < destination.Length; index++)
                {
                    destination[index] = inverseOutput[group.Discard + index] * scale;
                }

                _residualRotators[channelIndex].RotateInPlace(destination);
            }

            if (Diagnostics.IsTimingEnabled)
            {
                Diagnostics.RecordChannelProcessing(
                    System.Diagnostics.Stopwatch.GetTimestamp() - channelStartedAt);
            }
        }

        for (var channelIndex = 0; channelIndex < Plan.Channels.Count; channelIndex++)
        {
            WriteOutput(output, channelIndex, _outputs[channelIndex]);
        }
    }

    protected override void ResetCore()
    {
        _initialized = false;
        _ringHead = 0;
        _residualRotatorsAnchored = false;
    }

    protected override void DisposeCore()
    {
        foreach (var group in _inverseGroups)
        {
            group.BackwardPlan.Dispose();
        }

        _forwardPlan.Dispose();
    }

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

    private void UpdateSpectrumRing(ReadOnlySpan<ComplexF> input)
    {
        if (!_initialized)
        {
            _ringHead = 0;
            for (var age = 0; age < _spectrumRing.Length; age++)
            {
                var slot = FrequencyBinMath.Mod(-age, _spectrumRing.Length);
                var sourceOffset = checked(
                    InputRequirements.HistorySize - ((age + 1) * _partitionLength));
                ExecuteForwardWindow(input, sourceOffset, _spectrumRing[slot]);
            }

            _initialized = true;
            return;
        }

        _ringHead++;
        if (_ringHead == _spectrumRing.Length)
        {
            _ringHead = 0;
        }

        ExecuteForwardWindow(
            input,
            InputRequirements.HistorySize - _partitionLength,
            _spectrumRing[_ringHead]);
    }

    private void ExecuteForwardWindow(
        ReadOnlySpan<ComplexF> input,
        int sourceOffset,
        Span<ComplexF> destination)
    {
        var copyStartedAt = Diagnostics.BeginTiming();
        var forwardInput = _forwardPlan.WritableInput;
        forwardInput.Clear();
        var sourceStart = Math.Max(0, sourceOffset);
        var destinationStart = Math.Max(0, -sourceOffset);
        var copyCount = Math.Min(
            input.Length - sourceStart,
            _transformLength - destinationStart);
        if (copyCount > 0)
        {
            input.Slice(sourceStart, copyCount)
                .CopyTo(forwardInput.Slice(destinationStart, copyCount));
        }

        Diagnostics.RecordFdcInputCopy(
            checked(copyCount * 8),
            Diagnostics.IsTimingEnabled
                ? System.Diagnostics.Stopwatch.GetTimestamp() - copyStartedAt
                : 0);

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
                Diagnostics.RecordFftwExecution(
                    System.Diagnostics.Stopwatch.GetTimestamp() - fftStartedAt);
            }
        }

        ForwardTransformExecutionCount++;
        _forwardPlan.Output.CopyTo(destination);
    }

    private void ExecuteInverse(FftwComplexPlan plan)
    {
        var fftStartedAt = Diagnostics.BeginTiming();
        try
        {
            plan.ExecuteFromInput();
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
                Diagnostics.RecordFftwExecution(
                    System.Diagnostics.Stopwatch.GetTimestamp() - fftStartedAt);
            }
        }
    }

    private ReadOnlySpan<ComplexF> SpectrumAtAge(int age)
    {
        var slot = _ringHead - age;
        if (slot < 0)
        {
            slot += _spectrumRing.Length;
        }

        return _spectrumRing[slot];
    }

    private sealed class InverseGroup
    {
        public InverseGroup(
            int transformLength,
            int partitionLength,
            int decimation,
            int[] channelIndices)
        {
            Decimation = decimation;
            ChannelIndices = channelIndices;
            ShortLength = transformLength / decimation;
            Discard = partitionLength / decimation;
            BackwardPlan = new FftwComplexPlan(
                ShortLength,
                channelIndices.Length,
                FftwNative.Backward);
        }

        public int Decimation { get; }
        public int[] ChannelIndices { get; }
        public int ShortLength { get; }
        public int Discard { get; }
        public FftwComplexPlan BackwardPlan { get; }
    }
}
