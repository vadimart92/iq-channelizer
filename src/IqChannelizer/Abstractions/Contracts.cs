namespace IqChannelizer.Abstractions;

public enum ChannelizerStrategy
{
    Fdc,
    Pfb,
    Auto
}

public enum SimdPreference
{
    Auto,
    Scalar,
    Avx2,
    Avx512
}

public enum DiagnosticsMode
{
    Disabled,
    Counters,
    StageTiming
}

public enum PfbPrototypeDesignMode
{
    Conservative,
    FoldAware
}

public sealed record ChannelizerRequest(
    double InputSampleRateHz,
    IReadOnlyList<ChannelRequest> Channels,
    ChannelizerStrategy Strategy = ChannelizerStrategy.Fdc,
    InputBlockConstraints? InputBlocks = null,
    ChannelizerImplementationHints? Hints = null);

public sealed record InputBlockConstraints(
    int PreferredChunkSize = 8192,
    int MaxChunkSize = 32768);

public sealed record ChannelizerImplementationHints(
    int? FdcDecimationFactor = null,
    int? PfbFftSize = null,
    int? PfbHopSize = null,
    int? PfbFramesPerBatch = null,
    SimdPreference Simd = SimdPreference.Auto,
    DiagnosticsMode Diagnostics = DiagnosticsMode.Disabled,
    PfbPrototypeDesignMode PfbPrototypeDesign = PfbPrototypeDesignMode.Conservative);

public sealed record ChannelRequest(
    int ChannelId,
    double CenterFrequencyHz,
    double PassbandWidthHz,
    double TransitionWidthHz,
    double StopbandAttenuationDb = 80.0,
    double PassbandRippleDb = 0.1,
    double? MinimumOutputSampleRateHz = null,
    double? PreferredOutputSampleRateHz = null);

public readonly record struct InputRequirements
{
    public InputRequirements(int HistorySize, int ChunkSize)
    {
        if (HistorySize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(HistorySize));
        }

        if (ChunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ChunkSize));
        }

        if (HistorySize > int.MaxValue - ChunkSize)
        {
            throw new ArgumentOutOfRangeException(nameof(ChunkSize), "History and chunk sizes exceed the supported input span length.");
        }

        this.HistorySize = HistorySize;
        this.ChunkSize = ChunkSize;
    }

    public int HistorySize { get; }
    public int ChunkSize { get; }
    public int InputSize => checked(HistorySize + ChunkSize);
}

public readonly record struct RationalSampleOffset
{
    public RationalSampleOffset(long numerator, long denominator)
    {
        if (denominator == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator));
        }

        if (denominator < 0)
        {
            numerator = checked(-numerator);
            denominator = checked(-denominator);
        }

        var divisor = GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / divisor;
        Denominator = denominator / divisor;
    }

    public long Numerator { get; }
    public long Denominator { get; }

    private static long GreatestCommonDivisor(long left, long right)
    {
        left = Math.Abs(left);
        while (right != 0)
        {
            var remainder = left % right;
            left = right;
            right = remainder;
        }

        return left == 0 ? 1 : left;
    }
}

public interface IChannelOutputSink
{
    void Write(int channelId, ReadOnlySpan<ComplexF> samples);
}

public interface IStreamingChannelizer : IDisposable
{
    ResolvedChannelizerPlan Plan { get; }
    InputRequirements InputRequirements { get; }
    ChannelizerDiagnostics Diagnostics { get; }

    void Process(
        ReadOnlySpan<ComplexF> historyAndChunk,
        long firstNewSampleIndex,
        IChannelOutputSink output);

    void Reset(long nextFirstNewSampleIndex);
}
