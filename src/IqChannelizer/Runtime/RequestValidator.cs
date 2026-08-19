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

        if (!Enum.IsDefined(request.Strategy))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Unknown channelizer strategy.");
        }

        var ids = new HashSet<int>();
        foreach (var channel in request.Channels)
        {
            if (channel is null)
            {
                throw new ArgumentException("Channel entries must not be null.", nameof(request));
            }

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

            var occupiedWidth = channel.PassbandWidthHz + channel.TransitionWidthHz;
            if (!double.IsFinite(occupiedWidth) || occupiedWidth > request.InputSampleRateHz)
            {
                throw new ArgumentOutOfRangeException(nameof(request), $"Channel {channel.ChannelId} occupied width must be finite and no greater than the input sample rate.");
            }

            if (!double.IsFinite(channel.StopbandAttenuationDb) || channel.StopbandAttenuationDb <= 0 ||
                !double.IsFinite(channel.PassbandRippleDb) || channel.PassbandRippleDb <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request), $"Channel {channel.ChannelId} attenuation and ripple must be finite and positive.");
            }

            ValidateOptionalRate(channel.MinimumOutputSampleRateHz, channel.ChannelId, "minimum");
            ValidateOptionalRate(channel.PreferredOutputSampleRateHz, channel.ChannelId, "preferred");
            if (channel.MinimumOutputSampleRateHz is { } minimum &&
                channel.PreferredOutputSampleRateHz is { } preferred && preferred < minimum)
            {
                throw new ArgumentException($"Channel {channel.ChannelId} preferred output rate must not be below its minimum output rate.", nameof(request));
            }
        }

        var hints = request.Hints;
        if (hints is null)
        {
            return;
        }

        if (!Enum.IsDefined(hints.Simd))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Unknown SIMD preference.");
        }

        if (!Enum.IsDefined(hints.Diagnostics))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Unknown diagnostics mode.");
        }

        if (hints.FdcDecimationFactor is { } decimation &&
            (decimation <= 0 || (decimation & (decimation - 1)) != 0))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Forced FDC decimation must be a positive power of two.");
        }

        if (hints.PfbFftSize is { } fftSize && fftSize < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Forced PFB FFT size must be at least two.");
        }

        if (hints.PfbHopSize is { } hopSize && hopSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Forced PFB hop size must be positive.");
        }

        if (hints.PfbFramesPerBatch is { } frames && frames < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Forced PFB frames per batch must be positive.");
        }

        if (hints.PfbFftSize is { } forcedFft && hints.PfbHopSize is { } forcedHop && forcedHop > forcedFft)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Forced PFB hop size must not exceed its FFT size.");
        }

    }

    private static void ValidateOptionalRate(double? value, int channelId, string label)
    {
        if (value is { } rate && (!double.IsFinite(rate) || rate <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Channel {channelId} {label} output rate must be finite and positive.");
        }
    }
}
