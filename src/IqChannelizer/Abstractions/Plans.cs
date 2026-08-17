namespace IqChannelizer.Abstractions;

public sealed record ResolvedChannelPlan
{
    public required int ChannelId { get; init; }
    public required double RequestedCenterFrequencyHz { get; init; }
    public required double NormalizedCenterFrequencyHz { get; init; }
    public required double PassbandWidthHz { get; init; }
    public required double TransitionWidthHz { get; init; }
    public required double StopbandAttenuationDb { get; init; }
    public required double PassbandRippleDb { get; init; }
    public required double CoarseCenterFrequencyHz { get; init; }
    public required double CoarseOutputSampleRateHz { get; init; }
    public required double ResidualFrequencyHz { get; init; }
    public required double OutputSampleRateHz { get; init; }
    public required int OutputSamplesPerProcess { get; init; }
    public required int CoarseBin { get; init; }
    public required int DecimationFactor { get; init; }
    public required int FineDecimationFactor { get; init; }
    public int? ShortInverseFftLength { get; init; }
    public int? PfbGroupId { get; init; }
    public int? PfbFftSize { get; init; }
    public int? PfbHopSize { get; init; }
    public required string PrototypeFilterId { get; init; }
    public required string FineFilterId { get; init; }
    public required RationalSampleOffset GroupDelayInputSamples { get; init; }
    public required RationalSampleOffset FirstOutputInputSampleOffset { get; init; }
    public required RationalSampleOffset InputSamplesPerOutputSample { get; init; }
    public string? Warning { get; init; }
}

public sealed record ResolvedChannelizerPlan
{
    public required ChannelizerStrategy Strategy { get; init; }
    public required double InputSampleRateHz { get; init; }
    public required InputRequirements InputRequirements { get; init; }
    public required IReadOnlyList<ResolvedChannelPlan> Channels { get; init; }
    public required string DspBackend { get; init; }
    public required SimdPreference SelectedSimdBackend { get; init; }
    public required int ChunkAlignment { get; init; }
    public required int FftwThreadCount { get; init; }
    public required long AlignedBufferBytes { get; init; }
    public required long EstimatedWorkingSetBytes { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public int? FftSize { get; init; }
    public int? HopSize { get; init; }
    public int? FramesPerBatch { get; init; }
    public RationalSampleOffset? OversamplingRatio { get; init; }
    public string? PfbPhaseShiftMode { get; init; }
    public int? TapsPerPhase { get; init; }
    public string? FilterDesignMode { get; init; }
    public string? BenchmarkProfileKey { get; init; }
}
