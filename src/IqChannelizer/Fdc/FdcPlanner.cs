using IqChannelizer.Abstractions;

namespace IqChannelizer.Fdc;

internal sealed record FdcLayout(
    InputRequirements InputRequirements,
    int[] Decimations,
    float[][] Taps,
    int MaximumDecimation);

internal static class FdcPlanner
{
    public static FdcLayout CreateLayout(ChannelizerRequest request)
    {
        var constraints = request.InputBlocks ?? new InputBlockConstraints();
        var decimations = new int[request.Channels.Count];
        var taps = new float[request.Channels.Count][];
        var maximumDecimation = 1;
        for (var index = 0; index < request.Channels.Count; index++)
        {
            var channel = request.Channels[index];
            var decimation = request.Hints?.FdcDecimationFactor ?? SelectDecimation(
                request.InputSampleRateHz,
                channel,
                constraints.MaxChunkSize);
            ValidateOutputRate(channel, request.InputSampleRateHz / decimation);
            decimations[index] = decimation;
            maximumDecimation = Math.Max(maximumDecimation, decimation);
            taps[index] = FdcFilterDesign.DesignAlignedTaps(channel, request.InputSampleRateHz, decimation);
        }

        var maximumOrder = taps.Max(filter => filter.Length - 1);
        var history = checked(((maximumOrder + maximumDecimation - 1) / maximumDecimation) * maximumDecimation);
        var chunk = SelectChunk(history, maximumDecimation, constraints);
        return new FdcLayout(new InputRequirements(history, chunk), decimations, taps, maximumDecimation);
    }

    internal static bool IsSmoothTransformLength(int value)
    {
        if (value <= 0)
        {
            return false;
        }

        foreach (var factor in new[] { 2, 3, 5, 7 })
        {
            while (value % factor == 0)
            {
                value /= factor;
            }
        }

        return value == 1;
    }

    private static int SelectDecimation(double inputSampleRateHz, ChannelRequest channel, int maxChunkSize)
    {
        var targetRate = Math.Max(
            channel.PassbandWidthHz + channel.TransitionWidthHz,
            Math.Max(channel.MinimumOutputSampleRateHz ?? 0, channel.PreferredOutputSampleRateHz ?? 0));
        var selected = 1;
        while (selected <= maxChunkSize / 2)
        {
            var candidate = selected * 2;
            if (inputSampleRateHz / candidate < targetRate)
            {
                break;
            }

            selected = candidate;
        }

        return selected;
    }

    private static int SelectChunk(int history, int alignment, InputBlockConstraints constraints)
    {
        var best = 0;
        var bestDistance = long.MaxValue;
        var bestIsSmooth = false;
        for (var chunk = alignment; chunk <= constraints.MaxChunkSize; chunk += alignment)
        {
            var distance = Math.Abs((long)chunk - constraints.PreferredChunkSize);
            var isSmooth = IsSmoothTransformLength(checked(history + chunk));
            if (distance < bestDistance ||
                (distance == bestDistance && isSmooth && !bestIsSmooth) ||
                (distance == bestDistance && isSmooth == bestIsSmooth && chunk > best))
            {
                best = chunk;
                bestDistance = distance;
                bestIsSmooth = isSmooth;
            }
        }

        if (best > 0)
        {
            return best;
        }

        throw new ArgumentException("No FDC chunk satisfies the resolved decimation and block constraints.");
    }

    private static void ValidateOutputRate(ChannelRequest channel, double outputRate)
    {
        var required = Math.Max(channel.PassbandWidthHz + channel.TransitionWidthHz, channel.MinimumOutputSampleRateHz ?? 0);
        if (outputRate < required)
        {
            throw new ArgumentException($"Resolved output rate for channel {channel.ChannelId} is below its signal requirements.");
        }
    }
}
