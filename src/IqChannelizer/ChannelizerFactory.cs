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
        var layout = FdcPlanner.CreateLayout(request);
        var requirements = layout.InputRequirements;
        var history = requirements.HistorySize;
        var chunk = requirements.ChunkSize;
        var transformLength = requirements.InputSize;
        var channels = ResolveFdcChannels(request, layout.Decimations, transformLength, chunk, history);
        var designs = new FdcChannelDesign[channels.Count];
        for (var index = 0; index < designs.Length; index++)
        {
            var channelTaps = PadFilterToHistory(layout.Taps[index], history);
            designs[index] = FdcFilterDesign.Complete(
                request.Channels[index],
                channelTaps,
                request.InputSampleRateHz,
                layout.Decimations[index],
                transformLength,
                channels[index].ResidualFrequencyHz);
        }

        var sumShortLengths = channels.Sum(channel => (long)channel.ShortInverseFftLength!.Value);
        var outputValues = channels.Sum(channel => (long)channel.OutputSamplesPerProcess);
        var nativeBytes = checked(16L * (transformLength + sumShortLengths));
        var workingSetBytes = checked(nativeBytes + (8L * transformLength) + (24L * sumShortLengths) + (8L * outputValues));
        var plan = new ResolvedChannelizerPlan
        {
            Strategy = ChannelizerStrategy.Fdc,
            InputSampleRateHz = request.InputSampleRateHz,
            InputRequirements = requirements,
            Channels = channels,
            DspBackend = $"FFTW {FftwRuntime.Info.Version} single-precision C2C",
            SelectedSimdBackend = SimdPreference.Scalar,
            ChunkAlignment = layout.MaximumDecimation,
            FftwThreadCount = 1,
            AlignedBufferBytes = nativeBytes,
            EstimatedWorkingSetBytes = workingSetBytes,
            Warnings = Array.Empty<string>(),
            FftSize = transformLength,
            FilterDesignMode = "KaiserConservativeOverlapSave"
        };
        return new FftwFdcEngine(plan, designs);
    }

    private static IReadOnlyList<ResolvedChannelPlan> ResolveFdcChannels(
        ChannelizerRequest request,
        IReadOnlyList<int> decimations,
        int transformLength,
        int chunk,
        int history)
    {
        var result = new ResolvedChannelPlan[request.Channels.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var requested = request.Channels[index];
            var decimation = decimations[index];
            var outputRate = request.InputSampleRateHz / decimation;
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
        var prototype = PfbPrototypeDesign.Design(request, fftSize, hopSize);
        var fineStages = request.Channels
            .Select(channel => PfbFineStageDesigner.Design(channel, outputRate, frames))
            .ToArray();
        var channels = new ResolvedChannelPlan[request.Channels.Count];
        for (var index = 0; index < channels.Length; index++)
        {
            var requested = request.Channels[index];
            ValidateOutputRate(requested, outputRate);
            var bin = PfbMath.Mod((long)Math.Round(requested.CenterFrequencyHz * fftSize / request.InputSampleRateHz), fftSize);
            var signedBin = bin <= fftSize / 2 ? bin : bin - fftSize;
            var coarse = signedBin * request.InputSampleRateHz / fftSize;
            var fine = fineStages[index];
            var finalOutputRate = outputRate / fine.DecimationFactor;
            ValidateOutputRate(requested, finalOutputRate);
            var totalDelay = AddInputSampleDelays(
                prototype.GroupDelayInputSamples,
                fine.GroupDelayCoarseSamples,
                hopSize);
            channels[index] = ResolveChannel(
                requested, coarse, bin, finalOutputRate, frames / fine.DecimationFactor,
                checked(hopSize * fine.DecimationFactor),
                firstOutputOffset: checked(hopSize - 1 - ToIntegralSamples(totalDelay)),
                pfbGroupId: 0, pfbFftSize: fftSize, pfbHopSize: hopSize,
                prototypeFilterId: $"KaiserPfbK{fftSize}P{prototype.TapsPerPhase(fftSize)}",
                groupDelay: totalDelay,
                fineDecimationFactor: fine.DecimationFactor,
                fineFilterId: fine.FilterId,
                coarseOutputRate: outputRate);
        }

        var requirements = new InputRequirements(prototype.Taps.Length - 1, chunk);
        var transformValues = checked((long)fftSize * frames);
        if (transformValues > int.MaxValue)
        {
            throw new ArgumentException("PFB FFT size and frame count exceed the supported managed buffer length.", nameof(request));
        }

        var nativeBytes = checked(16L * transformValues);
        var workingSetBytes = checked(nativeBytes + (16L * transformValues) +
                                      (24L * frames * channels.Length) + (4L * prototype.Taps.Length) +
                                      fineStages.Sum(stage => 16L * stage.Taps.Length));
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
            Warnings = Array.Empty<string>(),
            FftSize = fftSize,
            HopSize = hopSize,
            FramesPerBatch = frames,
            OversamplingRatio = new RationalSampleOffset(fftSize, hopSize),
            PfbPhaseShiftMode = "PreFftCircularShift",
            TapsPerPhase = prototype.TapsPerPhase(fftSize),
            FilterDesignMode = "KaiserConservative"
        };
        return new FftwPfbEngine(plan, fftSize, hopSize, frames, prototype.Taps, fineStages);
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
        RationalSampleOffset? groupDelay = null,
        int fineDecimationFactor = 1,
        string fineFilterId = "Identity",
        double? coarseOutputRate = null)
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
            CoarseOutputSampleRateHz = coarseOutputRate ?? outputRate,
            ResidualFrequencyHz = request.CenterFrequencyHz - coarse,
            OutputSampleRateHz = outputRate,
            OutputSamplesPerProcess = outputCount,
            CoarseBin = bin,
            DecimationFactor = pfbHopSize ?? inputSamplesPerOutput,
            FineDecimationFactor = fineDecimationFactor,
            ShortInverseFftLength = shortInverseFftLength,
            PfbGroupId = pfbGroupId,
            PfbFftSize = pfbFftSize,
            PfbHopSize = pfbHopSize,
            PrototypeFilterId = prototypeFilterId ?? (shortInverseFftLength.HasValue ? "LengthOneFdcFixture" : "RectangularPfbP1Fixture"),
            FineFilterId = fineFilterId,
            GroupDelayInputSamples = groupDelay ?? new RationalSampleOffset(0, 1),
            FirstOutputInputSampleOffset = new RationalSampleOffset(firstOutputOffset, 1),
            InputSamplesPerOutputSample = new RationalSampleOffset(inputSamplesPerOutput, 1),
            Warning = warning
        };
    }

    private static RationalSampleOffset AddInputSampleDelays(
        RationalSampleOffset prototypeDelay,
        RationalSampleOffset fineDelayCoarseSamples,
        int hopSize)
    {
        var denominator = checked(prototypeDelay.Denominator * fineDelayCoarseSamples.Denominator);
        var numerator = checked(
            prototypeDelay.Numerator * fineDelayCoarseSamples.Denominator +
            fineDelayCoarseSamples.Numerator * hopSize * prototypeDelay.Denominator);
        return new RationalSampleOffset(numerator, denominator);
    }

    private static int ToIntegralSamples(RationalSampleOffset value)
    {
        if (value.Denominator != 1)
        {
            throw new NotSupportedException("The scalar PFB MVP requires integral total FIR delay.");
        }

        return checked((int)value.Numerator);
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

}
