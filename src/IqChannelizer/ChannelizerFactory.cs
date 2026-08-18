using System.Runtime.CompilerServices;
using IqChannelizer.Abstractions;
using IqChannelizer.Fdc;
using IqChannelizer.Fftw;
using IqChannelizer.Dsp;
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

        var taps = request.Channels
            .Select(channel => FdcFilterDesign.DesignAlignedTaps(channel, request.InputSampleRateHz, decimation))
            .ToArray();
        var history = taps.Max(item => item.Length - 1);
        if (history % decimation != 0)
        {
            throw new InvalidOperationException("FDC filter history must be divisible by its decimation factor.");
        }

        var requirements = new InputRequirements(history, chunk);
        var transformLength = requirements.InputSize;
        var channels = ResolveFdcChannels(request, decimation, transformLength, chunk, history);
        var designs = new FdcChannelDesign[channels.Count];
        for (var index = 0; index < designs.Length; index++)
        {
            var channelTaps = PadFilterToHistory(taps[index], history);
            designs[index] = FdcFilterDesign.Complete(
                request.Channels[index],
                channelTaps,
                request.InputSampleRateHz,
                decimation,
                transformLength,
                channels[index].ResidualFrequencyHz);
        }

        var shortLength = transformLength / decimation;
        var channelCount = channels.Count;
        var nativeBytes = checked(16L * (transformLength + ((long)shortLength * channelCount)));
        var workingSetBytes = checked(nativeBytes + (8L * (transformLength + (4L * shortLength * channelCount))));
        var plan = new ResolvedChannelizerPlan
        {
            Strategy = ChannelizerStrategy.Fdc,
            InputSampleRateHz = request.InputSampleRateHz,
            InputRequirements = requirements,
            Channels = channels,
            DspBackend = $"FFTW {FftwRuntime.Info.Version} single-precision C2C",
            SelectedSimdBackend = SimdPreference.Scalar,
            ChunkAlignment = decimation,
            FftwThreadCount = 1,
            AlignedBufferBytes = nativeBytes,
            EstimatedWorkingSetBytes = workingSetBytes,
            Warnings = Array.Empty<string>(),
            FftSize = transformLength,
            FilterDesignMode = "KaiserConservativeOverlapSave"
        };
        return new FftwFdcEngine(plan, decimation, designs);
    }

    private static IReadOnlyList<ResolvedChannelPlan> ResolveFdcChannels(
        ChannelizerRequest request,
        int decimation,
        int transformLength,
        int chunk,
        int history)
    {
        var outputRate = request.InputSampleRateHz / decimation;
        var result = new ResolvedChannelPlan[request.Channels.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var requested = request.Channels[index];
            ValidateOutputRate(requested, outputRate);
            var bin = (int)Math.Round(requested.CenterFrequencyHz * transformLength / request.InputSampleRateHz);
            var normalizedBin = PfbMath.Mod(bin, transformLength);
            var signedBin = normalizedBin <= transformLength / 2 ? normalizedBin : normalizedBin - transformLength;
            var coarse = signedBin * request.InputSampleRateHz / transformLength;
            result[index] = ResolveChannel(
                requested, coarse, normalizedBin, outputRate, chunk / decimation, decimation,
                firstOutputOffset: -history / 2, shortInverseFftLength: transformLength / decimation,
                prototypeFilterId: $"KaiserFdcOrder{history}", groupDelay: new RationalSampleOffset(history, 2));
        }

        return Array.AsReadOnly(result);
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

        int chunk;
        try
        {
            chunk = checked(frames * hopSize);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException("PFB frames and hop size overflow the supported chunk size.", nameof(request), exception);
        }
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
            channels[index] = ResolveChannel(
                requested, coarse, bin, outputRate, frames, hopSize,
                firstOutputOffset: hopSize - 1, pfbGroupId: 0, pfbFftSize: fftSize, pfbHopSize: hopSize);
        }

        var requirements = new InputRequirements(fftSize - 1, chunk);
        var transformValues = checked((long)fftSize * frames);
        if (transformValues > int.MaxValue)
        {
            throw new ArgumentException("PFB FFT size and frame count exceed the supported managed buffer length.", nameof(request));
        }

        var nativeBytes = checked(16L * transformValues);
        var workingSetBytes = checked(nativeBytes + (16L * transformValues) +
                                      (8L * frames * channels.Length) + (4L * fftSize));
        var plan = new ResolvedChannelizerPlan
        {
            Strategy = ChannelizerStrategy.Pfb,
            InputSampleRateHz = request.InputSampleRateHz,
            InputRequirements = requirements,
            Channels = Array.AsReadOnly(channels),
            DspBackend = $"FFTW {FftwRuntime.Info.Version} single-precision batched C2C with scalar PFB FIR",
            SelectedSimdBackend = SimdPreference.Scalar,
            ChunkAlignment = hopSize,
            FftwThreadCount = 1,
            AlignedBufferBytes = nativeBytes,
            EstimatedWorkingSetBytes = workingSetBytes,
            Warnings = Array.AsReadOnly(["PFB currently uses a one-tap-per-phase rectangular algebra fixture, not a production prototype filter."]),
            FftSize = fftSize,
            HopSize = hopSize,
            FramesPerBatch = frames,
            OversamplingRatio = new RationalSampleOffset(fftSize, hopSize),
            PfbPhaseShiftMode = "PreFftCircularShift",
            TapsPerPhase = 1,
            FilterDesignMode = "RectangularAlgebraFixture"
        };
        return new FftwPfbEngine(plan, fftSize, hopSize, frames);
    }

    private static ResolvedChannelPlan ResolveChannel(
        ChannelRequest request,
        double coarse,
        int bin,
        double outputRate,
        int outputCount,
        int inputSamplesPerOutput,
        int firstOutputOffset,
        int? shortInverseFftLength = null,
        int? pfbGroupId = null,
        int? pfbFftSize = null,
        int? pfbHopSize = null,
        string? prototypeFilterId = null,
        RationalSampleOffset? groupDelay = null)
    {
        var warning = request.PreferredOutputSampleRateHz is { } preferred && outputRate < preferred
            ? $"Resolved output rate {outputRate:R} Hz is below the preferred rate {preferred:R} Hz."
            : null;
        return new ResolvedChannelPlan
        {
            ChannelId = request.ChannelId,
            RequestedCenterFrequencyHz = request.CenterFrequencyHz,
            NormalizedCenterFrequencyHz = request.CenterFrequencyHz,
            PassbandWidthHz = request.PassbandWidthHz,
            TransitionWidthHz = request.TransitionWidthHz,
            StopbandAttenuationDb = request.StopbandAttenuationDb,
            PassbandRippleDb = request.PassbandRippleDb,
            CoarseCenterFrequencyHz = coarse,
            CoarseOutputSampleRateHz = outputRate,
            ResidualFrequencyHz = request.CenterFrequencyHz - coarse,
            OutputSampleRateHz = outputRate,
            OutputSamplesPerProcess = outputCount,
            CoarseBin = bin,
            DecimationFactor = inputSamplesPerOutput,
            FineDecimationFactor = 1,
            ShortInverseFftLength = shortInverseFftLength,
            PfbGroupId = pfbGroupId,
            PfbFftSize = pfbFftSize,
            PfbHopSize = pfbHopSize,
            PrototypeFilterId = prototypeFilterId ?? (shortInverseFftLength.HasValue ? "LengthOneFdcFixture" : "RectangularPfbP1Fixture"),
            FineFilterId = "Identity",
            GroupDelayInputSamples = groupDelay ?? new RationalSampleOffset(0, 1),
            FirstOutputInputSampleOffset = new RationalSampleOffset(firstOutputOffset, 1),
            InputSamplesPerOutputSample = new RationalSampleOffset(inputSamplesPerOutput, 1),
            Warning = warning
        };
    }

    private static float[] PadFilterToHistory(float[] taps, int history)
    {
        var order = taps.Length - 1;
        if (order == history)
        {
            return taps;
        }

        var result = new float[history + 1];
        taps.CopyTo(result, (history - order) / 2);
        return result;
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
