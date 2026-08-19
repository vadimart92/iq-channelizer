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
    private readonly record struct FdcDesignKey(
        double PassbandWidthHz,
        double TransitionWidthHz,
        double StopbandAttenuationDb,
        double PassbandRippleDb,
        int Decimation,
        double ResidualFrequencyHz);

    private readonly record struct PartitionedFdcDesignKey(
        double PassbandWidthHz,
        double TransitionWidthHz,
        double StopbandAttenuationDb,
        double PassbandRippleDb,
        int Decimation,
        double ResidualFrequencyHz,
        bool OddCoarseBin);

    public static IStreamingChannelizer Create(ChannelizerRequest request)
    {
        RequestValidator.Validate(request);
        if (Unsafe.SizeOf<ComplexF>() != 8)
        {
            throw new PlatformNotSupportedException("ComplexF must contain exactly two adjacent 32-bit floats.");
        }

        var selection = request.Strategy == ChannelizerStrategy.Auto
            ? StrategyProfileSelector.Resolve(request)
            : null;
        var resolvedRequest = selection is null
            ? request
            : request with { Strategy = selection.Strategy };
        return resolvedRequest.Strategy switch
        {
            ChannelizerStrategy.Fdc => CreateFdc(resolvedRequest, selection),
            ChannelizerStrategy.Pfb => CreatePfb(resolvedRequest, selection),
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
    }

    private static IStreamingChannelizer CreateFdc(
        ChannelizerRequest request,
        StrategySelection? strategySelection = null)
    {
        // End-to-end evidence favors AVX2 slightly for FDC even though the isolated AVX-512 extraction kernel is faster.
        var simdBackend = SimdBackendResolver.Resolve(
            request.Hints?.Simd ?? SimdPreference.Auto,
            autoPreferAvx512: false);
        var layout = FdcPlanner.CreateLayout(request);
        var requirements = layout.InputRequirements;
        var history = requirements.HistorySize;
        var chunk = requirements.ChunkSize;
        var usePartitionedOverlapSave = history >= checked(2 * chunk);
        var transformLength = usePartitionedOverlapSave
            ? checked(2 * chunk)
            : requirements.InputSize;
        var channels = ResolveFdcChannels(request, layout.Decimations, transformLength, chunk, history);
        if (usePartitionedOverlapSave)
        {
            return CreatePartitionedFdc(
                request,
                strategySelection,
                layout,
                channels,
                simdBackend,
                transformLength);
        }

        var designs = new FdcChannelDesign[channels.Count];
        var completedDesigns = new Dictionary<FdcDesignKey, FdcChannelDesign>();
        for (var index = 0; index < designs.Length; index++)
        {
            var requested = request.Channels[index];
            var key = new FdcDesignKey(
                requested.PassbandWidthHz,
                requested.TransitionWidthHz,
                requested.StopbandAttenuationDb,
                requested.PassbandRippleDb,
                layout.Decimations[index],
                channels[index].ResidualFrequencyHz);
            if (!completedDesigns.TryGetValue(key, out var design))
            {
                var channelTaps = PadFilterToHistory(layout.Taps[index], history);
                design = FdcFilterDesign.Complete(
                    requested,
                    channelTaps,
                    request.InputSampleRateHz,
                    layout.Decimations[index],
                    transformLength,
                    channels[index].ResidualFrequencyHz);
                completedDesigns.Add(key, design);
            }

            designs[index] = design;
        }

        var sumShortLengths = channels.Sum(channel => (long)channel.ShortInverseFftLength!.Value);
        var outputValues = channels.Sum(channel => (long)channel.OutputSamplesPerProcess);
        var nativeBytes = checked(16L * (transformLength + sumShortLengths));
        var uniqueWindowValues = completedDesigns.Values.Sum(design => (long)design.SpectralWindow.Length);
        var workingSetBytes = checked(nativeBytes + (8L * uniqueWindowValues) + (8L * outputValues));
        var plan = new ResolvedChannelizerPlan
        {
            Strategy = ChannelizerStrategy.Fdc,
            InputSampleRateHz = request.InputSampleRateHz,
            InputRequirements = requirements,
            Channels = channels,
            DspBackend = simdBackend switch
            {
                SimdPreference.Avx512 => $"FFTW {FftwRuntime.Info.Version} single-precision C2C with AVX-512F extraction",
                SimdPreference.Avx2 => $"FFTW {FftwRuntime.Info.Version} single-precision C2C with AVX2/FMA extraction",
                _ => $"FFTW {FftwRuntime.Info.Version} single-precision C2C with scalar extraction"
            },
            SelectedSimdBackend = simdBackend,
            ChunkAlignment = layout.MaximumDecimation,
            FftwThreadCount = 1,
            AlignedBufferBytes = nativeBytes,
            EstimatedWorkingSetBytes = workingSetBytes,
            Warnings = Array.AsReadOnly(channels.Select(channel => channel.Warning)
                .Where(warning => warning is not null)
                .Select(warning => warning!)
                .Concat(strategySelection is null ? [] : [strategySelection.Explanation])
                .ToArray()),
            FftSize = transformLength,
            FilterDesignMode = "KaiserConservativeOverlapSave",
            BenchmarkProfileKey = strategySelection?.ProfileKey
        };
        return new FftwFdcEngine(plan, designs, simdBackend, request.Hints?.Diagnostics ?? DiagnosticsMode.Disabled);
    }

    private static IStreamingChannelizer CreatePartitionedFdc(
        ChannelizerRequest request,
        StrategySelection? strategySelection,
        FdcLayout layout,
        IReadOnlyList<ResolvedChannelPlan> channels,
        SimdPreference simdBackend,
        int transformLength)
    {
        var requirements = layout.InputRequirements;
        var history = requirements.HistorySize;
        var chunk = requirements.ChunkSize;
        var designs = new PartitionedFdcChannelDesign[channels.Count];
        var completedDesigns = new Dictionary<PartitionedFdcDesignKey, PartitionedFdcChannelDesign>();
        for (var index = 0; index < designs.Length; index++)
        {
            var requested = request.Channels[index];
            var channel = channels[index];
            var key = new PartitionedFdcDesignKey(
                requested.PassbandWidthHz,
                requested.TransitionWidthHz,
                requested.StopbandAttenuationDb,
                requested.PassbandRippleDb,
                layout.Decimations[index],
                channel.ResidualFrequencyHz,
                (channel.CoarseBin & 1) != 0);
            if (!completedDesigns.TryGetValue(key, out var design))
            {
                var channelTaps = PadFilterToHistory(layout.Taps[index], history);
                design = PartitionedFdcFilterDesign.Complete(
                    requested,
                    channelTaps,
                    request.InputSampleRateHz,
                    layout.Decimations[index],
                    chunk,
                    channel.CoarseBin,
                    channel.ResidualFrequencyHz);
                completedDesigns.Add(key, design);
            }

            designs[index] = design;
        }

        var partitionCount = checked((history + 1 + chunk - 1) / chunk);
        var sumShortLengths = channels.Sum(channel => (long)channel.ShortInverseFftLength!.Value);
        var outputValues = channels.Sum(channel => (long)channel.OutputSamplesPerProcess);
        var nativeBytes = checked(16L * (transformLength + sumShortLengths));
        var ringValues = checked((long)partitionCount * transformLength);
        var uniqueWindowValues = completedDesigns.Values.Sum(design =>
            design.PartitionSpectralWindows.Sum(window => (long)window.Length));
        var workingSetBytes = checked(
            nativeBytes + (8L * ringValues) + (8L * uniqueWindowValues) + (8L * outputValues));
        var plan = new ResolvedChannelizerPlan
        {
            Strategy = ChannelizerStrategy.Fdc,
            InputSampleRateHz = request.InputSampleRateHz,
            InputRequirements = requirements,
            Channels = channels,
            DspBackend = simdBackend switch
            {
                SimdPreference.Avx512 =>
                    $"FFTW {FftwRuntime.Info.Version} partitioned overlap-save with AVX-512F accumulation",
                SimdPreference.Avx2 =>
                    $"FFTW {FftwRuntime.Info.Version} partitioned overlap-save with AVX2/FMA accumulation",
                _ => $"FFTW {FftwRuntime.Info.Version} partitioned overlap-save with scalar accumulation"
            },
            SelectedSimdBackend = simdBackend,
            ChunkAlignment = layout.MaximumDecimation,
            FftwThreadCount = 1,
            AlignedBufferBytes = nativeBytes,
            EstimatedWorkingSetBytes = workingSetBytes,
            Warnings = Array.AsReadOnly(channels.Select(channel => channel.Warning)
                .Where(warning => warning is not null)
                .Select(warning => warning!)
                .Concat(strategySelection is null ? [] : [strategySelection.Explanation])
                .ToArray()),
            FftSize = transformLength,
            FilterDesignMode = "KaiserConservativePartitionedOverlapSave",
            BenchmarkProfileKey = strategySelection?.ProfileKey
        };
        return new PartitionedFftwFdcEngine(
            plan,
            designs,
            simdBackend,
            request.Hints?.Diagnostics ?? DiagnosticsMode.Disabled);
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
            var normalizedBin = FrequencyBinMath.NearestNormalizedBin(
                requested.CenterFrequencyHz,
                request.InputSampleRateHz,
                transformLength);
            var signedBin = FrequencyBinMath.ToSignedBin(normalizedBin, transformLength);
            var coarse = signedBin * request.InputSampleRateHz / transformLength;
            var residual = FrequencyBinMath.WrappedDifference(
                requested.CenterFrequencyHz,
                coarse,
                request.InputSampleRateHz);
            result[index] = ResolveChannel(
                requested, coarse, normalizedBin, outputRate, chunk / decimation, decimation,
                firstOutputOffset: -history / 2, shortInverseFftLength: transformLength / decimation,
                prototypeFilterId: $"KaiserFdcOrder{history}", groupDelay: new RationalSampleOffset(history, 2),
                residualFrequency: residual);
        }

        return Array.AsReadOnly(result);
    }

    private static IStreamingChannelizer CreatePfb(
        ChannelizerRequest request,
        StrategySelection? strategySelection = null)
    {
        // The AVX-512 PFB FIR advantage survives the full engine benchmark.
        var simdBackend = SimdBackendResolver.Resolve(
            request.Hints?.Simd ?? SimdPreference.Auto,
            autoPreferAvx512: true);
        var layout = PfbPlanner.CreateLayout(request);
        var fftSize = layout.FftSize;
        var hopSize = layout.HopSize;
        var frames = layout.FramesPerBatch;
        var chunk = layout.InputRequirements.ChunkSize;
        var outputRate = request.InputSampleRateHz / hopSize;
        var prototype = layout.Prototype;
        var fineStages = layout.FineStages;
        var channels = new ResolvedChannelPlan[request.Channels.Count];
        for (var index = 0; index < channels.Length; index++)
        {
            var requested = request.Channels[index];
            ValidateOutputRate(requested, outputRate);
            var bin = FrequencyBinMath.NearestNormalizedBin(
                requested.CenterFrequencyHz,
                request.InputSampleRateHz,
                fftSize);
            var signedBin = FrequencyBinMath.ToSignedBin(bin, fftSize);
            var coarse = signedBin * request.InputSampleRateHz / fftSize;
            var residual = FrequencyBinMath.WrappedDifference(
                requested.CenterFrequencyHz,
                coarse,
                request.InputSampleRateHz);
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
                prototypeFilterId:
                    $"Kaiser{prototype.DesignMode}PfbK{fftSize}P{prototype.TapsPerPhase(fftSize)}",
                groupDelay: totalDelay,
                fineDecimationFactor: fine.DecimationFactor,
                fineFilterId: fine.FilterId,
                coarseOutputRate: outputRate,
                residualFrequency: residual);
        }

        var requirements = layout.InputRequirements;
        var transformValues = checked((long)fftSize * frames);
        if (transformValues > int.MaxValue)
        {
            throw new ArgumentException("PFB FFT size and frame count exceed the supported managed buffer length.", nameof(request));
        }

        var nativeBytes = checked(16L * transformValues);
        var simdCoefficientBytes = simdBackend is SimdPreference.Avx2 or SimdPreference.Avx512
            ? 8L * prototype.Taps.Length
            : 0;
        var uniqueBinCount = channels.Select(channel => channel.CoarseBin).Distinct().Count();
        var rotationChannelCount = channels.Count(channel => channel.ResidualFrequencyHz != 0);
        var routeBytes = checked(4L * (uniqueBinCount + channels.Length));
        var streamBytes = checked(8L * frames * (uniqueBinCount + rotationChannelCount));
        var outputBytes = checked(8L * channels.Sum(channel => (long)channel.OutputSamplesPerProcess));
        var fineTapBytes = checked(4L * fineStages.Sum(stage => (long)stage.Taps.Length));
        var fineStateBytes = fineStages.Sum(stage =>
            stage.DecimationFactor == 1 && stage.Taps.Length == 1 && stage.Taps[0] == 1f
                ? 0L
                : checked((16L * (stage.Taps.Length - 1)) + (8L * frames)));
        var workingSetBytes = checked(
            nativeBytes +
            (4L * prototype.Taps.Length) +
            simdCoefficientBytes +
            routeBytes +
            streamBytes +
            outputBytes +
            fineTapBytes +
            fineStateBytes);
        var plan = new ResolvedChannelizerPlan
        {
            Strategy = ChannelizerStrategy.Pfb,
            InputSampleRateHz = request.InputSampleRateHz,
            InputRequirements = requirements,
            Channels = Array.AsReadOnly(channels),
            DspBackend = simdBackend switch
            {
                SimdPreference.Avx512 =>
                    $"FFTW {FftwRuntime.Info.Version} single-precision batched C2C with AVX-512F PFB FIR",
                SimdPreference.Avx2 =>
                    $"FFTW {FftwRuntime.Info.Version} single-precision batched C2C with AVX2/FMA PFB FIR",
                _ => $"FFTW {FftwRuntime.Info.Version} single-precision batched C2C with scalar PFB FIR"
            },
            SelectedSimdBackend = simdBackend,
            ChunkAlignment = hopSize,
            FftwThreadCount = 1,
            AlignedBufferBytes = nativeBytes,
            EstimatedWorkingSetBytes = workingSetBytes,
            Warnings = Array.AsReadOnly(layout.Warnings
                .Concat(channels.Select(channel => channel.Warning)
                    .Where(warning => warning is not null)
                    .Select(warning => warning!))
                .Concat(strategySelection is null ? [] : [strategySelection.Explanation])
                .ToArray()),
            FftSize = fftSize,
            HopSize = hopSize,
            FramesPerBatch = frames,
            OversamplingRatio = new RationalSampleOffset(fftSize, hopSize),
            PfbPhaseShiftMode = "PreFftCircularShift",
            TapsPerPhase = prototype.TapsPerPhase(fftSize),
            FilterDesignMode = $"Kaiser{prototype.DesignMode}",
            BenchmarkProfileKey = strategySelection?.ProfileKey
        };
        return new FftwPfbEngine(
            plan,
            fftSize,
            hopSize,
            frames,
            prototype.Taps,
            fineStages,
            simdBackend,
            request.Hints?.Diagnostics ?? DiagnosticsMode.Disabled);
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
        double? coarseOutputRate = null,
        double? residualFrequency = null)
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
            ResidualFrequencyHz = residualFrequency ?? request.CenterFrequencyHz - coarse,
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
