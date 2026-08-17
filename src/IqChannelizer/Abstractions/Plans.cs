namespace IqChannelizer.Abstractions;

public sealed record ResolvedChannelPlan
{
    public required int ChannelId { get; init; }
    public required double RequestedCenterFrequencyHz { get; init; }
    public required double CoarseCenterFrequencyHz { get; init; }
    public required double ResidualFrequencyHz { get; init; }
    public required double OutputSampleRateHz { get; init; }
    public required int OutputSamplesPerProcess { get; init; }
    public required int CoarseBin { get; init; }
    public required int DecimationFactor { get; init; }
    public required RationalSampleOffset GroupDelayInputSamples { get; init; }
    public required RationalSampleOffset InputSamplesPerOutputSample { get; init; }
}

public sealed record ResolvedChannelizerPlan
{
    public required ChannelizerStrategy Strategy { get; init; }
    public required double InputSampleRateHz { get; init; }
    public required InputRequirements InputRequirements { get; init; }
    public required IReadOnlyList<ResolvedChannelPlan> Channels { get; init; }
    public required string DspBackend { get; init; }
    public int? FftSize { get; init; }
    public int? HopSize { get; init; }
    public int? FramesPerBatch { get; init; }
}
