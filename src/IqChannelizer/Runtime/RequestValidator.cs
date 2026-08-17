using IqChannelizer.Abstractions;

namespace IqChannelizer.Runtime;

internal static class RequestValidator
{
    public static void Validate(ChannelizerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Channels);

        if (!double.IsFinite(request.InputSampleRateHz) || request.InputSampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Input sample rate must be finite and positive.");
        }

        if (request.Channels.Count == 0)
        {
            throw new ArgumentException("At least one channel is required.", nameof(request));
        }

        var constraints = request.InputBlocks ?? new InputBlockConstraints();
        if (constraints.PreferredChunkSize <= 0 || constraints.MaxChunkSize <= 0 ||
            constraints.PreferredChunkSize > constraints.MaxChunkSize)
        {
            throw new ArgumentException("Chunk constraints must be positive and preferred must not exceed maximum.", nameof(request));
        }

        var ids = new HashSet<int>();
        foreach (var channel in request.Channels)
        {
            if (!ids.Add(channel.ChannelId))
            {
                throw new ArgumentException($"Duplicate channel id {channel.ChannelId}.", nameof(request));
            }

            if (!double.IsFinite(channel.CenterFrequencyHz) ||
                channel.CenterFrequencyHz < -request.InputSampleRateHz / 2 ||
                channel.CenterFrequencyHz >= request.InputSampleRateHz / 2)
            {
                throw new ArgumentOutOfRangeException(nameof(request), $"Channel {channel.ChannelId} center is outside [-Fs/2, Fs/2).");
            }

            if (!double.IsFinite(channel.PassbandWidthHz) || channel.PassbandWidthHz <= 0 ||
                !double.IsFinite(channel.TransitionWidthHz) || channel.TransitionWidthHz <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request), $"Channel {channel.ChannelId} bandwidths must be finite and positive.");
            }
        }

        if (request.Hints?.Simd is SimdPreference.Avx2 or SimdPreference.Avx512)
        {
            throw new NotSupportedException("SIMD backends are intentionally not part of the scalar foundation.");
        }
    }
}
