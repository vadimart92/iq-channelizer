using System.Runtime.CompilerServices;
using IqChannelizer.Abstractions;
using IqChannelizer.Fdc;
using IqChannelizer.Pfb;
using IqChannelizer.Runtime;

namespace IqChannelizer;

public static class ChannelizerFactory
{
    public static IStreamingChannelizer Create(ChannelizerRequest request)
    {
        RequestValidator.Validate(request);
        if (Unsafe.SizeOf<ComplexF>() != 8)
        {
            throw new PlatformNotSupportedException("ComplexF must contain exactly two adjacent 32-bit floats.");
        }

        return request.Strategy switch
        {
            ChannelizerStrategy.Fdc => CreateFdc(request),
            ChannelizerStrategy.Pfb => CreatePfb(request),
            ChannelizerStrategy.Auto => throw new NotSupportedException("Auto strategy requires benchmark profiles and is not implemented yet."),
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
    }

    private static IStreamingChannelizer CreateFdc(ChannelizerRequest request)
    {
        var constraints = request.InputBlocks ?? new InputBlockConstraints();
        var decimation = request.Hints?.FdcDecimationFactor ?? 1;
        ValidatePowerOfTwo(decimation, nameof(ChannelizerImplementationHints.FdcDecimationFactor));
        var chunk = constraints.PreferredChunkSize - (constraints.PreferredChunkSize % decimation);
        if (chunk <= 0 || chunk > constraints.MaxChunkSize)
        {
            throw new ArgumentException("No FDC chunk satisfies the requested decimation and block constraints.", nameof(request));
        }

        var requirements = new InputRequirements(0, chunk);
        var channels = ResolveFdcChannels(request, decimation, chunk);
        var plan = new ResolvedChannelizerPlan
        {
            Strategy = ChannelizerStrategy.Fdc,
            InputSampleRateHz = request.InputSampleRateHz,
            InputRequirements = requirements,
            Channels = channels,
            DspBackend = "FFTW single-precision C2C",
            FftSize = chunk
        };
        return new FftwFdcEngine(plan, decimation);
    }

    private static IReadOnlyList<ResolvedChannelPlan> ResolveFdcChannels(ChannelizerRequest request, int decimation, int chunk)
    {
        var outputRate = request.InputSampleRateHz / decimation;
        var result = new ResolvedChannelPlan[request.Channels.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var requested = request.Channels[index];
            ValidateOutputRate(requested, outputRate);
            var bin = (int)Math.Round(requested.CenterFrequencyHz * chunk / request.InputSampleRateHz);
            var normalizedBin = PfbMath.Mod(bin, chunk);
            var signedBin = normalizedBin <= chunk / 2 ? normalizedBin : normalizedBin - chunk;
            var coarse = signedBin * request.InputSampleRateHz / chunk;
            result[index] = ResolveChannel(requested, coarse, normalizedBin, outputRate, chunk / decimation, decimation);
        }

        return result;
    }

    private static IStreamingChannelizer CreatePfb(ChannelizerRequest request)
    {
        var constraints = request.InputBlocks ?? new InputBlockConstraints();
        var fftSize = request.Hints?.PfbFftSize ?? 64;
        var hopSize = request.Hints?.PfbHopSize ?? fftSize / 2;
        var frames = request.Hints?.PfbFramesPerBatch ?? Math.Max(1, constraints.PreferredChunkSize / hopSize);
        if (fftSize < 2 || hopSize < 1 || hopSize > fftSize || frames < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "PFB requires K >= 2, 1 <= H <= K, and at least one frame.");
        }

        var chunk = checked(frames * hopSize);
        if (chunk > constraints.MaxChunkSize)
        {
            throw new ArgumentException("PFB frames do not fit MaxChunkSize.", nameof(request));
        }

        var outputRate = request.InputSampleRateHz / hopSize;
        var channels = new ResolvedChannelPlan[request.Channels.Count];
        for (var index = 0; index < channels.Length; index++)
        {
            var requested = request.Channels[index];
            ValidateOutputRate(requested, outputRate);
            var bin = PfbMath.Mod((long)Math.Round(requested.CenterFrequencyHz * fftSize / request.InputSampleRateHz), fftSize);
            var signedBin = bin <= fftSize / 2 ? bin : bin - fftSize;
            var coarse = signedBin * request.InputSampleRateHz / fftSize;
            channels[index] = ResolveChannel(requested, coarse, bin, outputRate, frames, hopSize);
        }

        var requirements = new InputRequirements(fftSize - 1, chunk);
        var plan = new ResolvedChannelizerPlan
        {
            Strategy = ChannelizerStrategy.Pfb,
            InputSampleRateHz = request.InputSampleRateHz,
            InputRequirements = requirements,
            Channels = channels,
            DspBackend = "FFTW single-precision batched C2C with scalar PFB FIR",
            FftSize = fftSize,
            HopSize = hopSize,
            FramesPerBatch = frames
        };
        return new FftwPfbEngine(plan, fftSize, hopSize, frames);
    }

    private static ResolvedChannelPlan ResolveChannel(ChannelRequest request, double coarse, int bin, double outputRate, int outputCount, int inputSamplesPerOutput)
    {
        return new ResolvedChannelPlan
        {
            ChannelId = request.ChannelId,
            RequestedCenterFrequencyHz = request.CenterFrequencyHz,
            CoarseCenterFrequencyHz = coarse,
            ResidualFrequencyHz = request.CenterFrequencyHz - coarse,
            OutputSampleRateHz = outputRate,
            OutputSamplesPerProcess = outputCount,
            CoarseBin = bin,
            DecimationFactor = inputSamplesPerOutput,
            GroupDelayInputSamples = new RationalSampleOffset(0, 1),
            InputSamplesPerOutputSample = new RationalSampleOffset(inputSamplesPerOutput, 1)
        };
    }

    private static void ValidateOutputRate(ChannelRequest channel, double outputRate)
    {
        var required = Math.Max(channel.PassbandWidthHz + channel.TransitionWidthHz, channel.MinimumOutputSampleRateHz ?? 0);
        if (outputRate < required)
        {
            throw new ArgumentException($"Resolved output rate for channel {channel.ChannelId} is below its signal requirements.");
        }
    }

    private static void ValidatePowerOfTwo(int value, string name)
    {
        if (value <= 0 || (value & (value - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(name, "The scalar FDC MVP accepts power-of-two decimation factors.");
        }
    }
}
