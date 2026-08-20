namespace IqChannelizer.Cli;

internal sealed class ProcessingSpeedStatistics(double inputSampleRateHz, int samplesPerBlock)
{
    private double totalSeconds;
    private double minimumRatio = double.PositiveInfinity;
    private double maximumRatio;

    public int BlockCount { get; private set; }

    public double AverageRatio => BlockCount == 0
        ? 0
        : checked((double)BlockCount * samplesPerBlock) / totalSeconds / inputSampleRateHz;

    public double MinimumRatio => BlockCount == 0 ? 0 : minimumRatio;
    public double MaximumRatio => BlockCount == 0 ? 0 : maximumRatio;
    public double AverageSamplesPerSecond => AverageRatio * inputSampleRateHz;

    public void Record(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        var ratio = samplesPerBlock / elapsed.TotalSeconds / inputSampleRateHz;
        totalSeconds += elapsed.TotalSeconds;
        minimumRatio = Math.Min(minimumRatio, ratio);
        maximumRatio = Math.Max(maximumRatio, ratio);
        BlockCount++;
    }
}
