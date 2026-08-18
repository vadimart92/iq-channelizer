using IqChannelizer.Abstractions;

namespace IqChannelizer.Pfb;

internal sealed record PfbLayout(
    int FftSize,
    int HopSize,
    int FramesPerBatch,
    InputRequirements InputRequirements,
    PfbPrototype Prototype,
    PfbFineStageDesign[] FineStages,
    IReadOnlyList<string> Warnings);

internal static class PfbPlanner
{
    private const int MaximumAutomaticFftSize = 8192;
    private const int MaximumHopCandidatesPerFftSize = 8;

    private readonly record struct Candidate(
        int FftSize,
        int HopSize,
        int FramesPerBatch,
        int ChunkSize,
        double Score);

    public static PfbLayout CreateLayout(ChannelizerRequest request)
    {
        var constraints = request.InputBlocks ?? new InputBlockConstraints();
        var candidates = EnumerateCandidates(request, constraints).ToArray();
        if (candidates.Length == 0)
        {
            throw new ArgumentException("No PFB K/H/FramesPerBatch candidate satisfies the block and single-bin feasibility constraints.", nameof(request));
        }

        Exception? lastFailure = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var prototype = PfbPrototypeDesign.Design(request, candidate.FftSize, candidate.HopSize);
                var coarseRate = request.InputSampleRateHz / candidate.HopSize;
                var fineStages = request.Channels
                    .Select(channel => PfbFineStageDesigner.Design(channel, coarseRate, candidate.FramesPerBatch))
                    .ToArray();
                var requirements = new InputRequirements(prototype.Taps.Length - 1, candidate.ChunkSize);
                var fullyForcedShape = request.Hints?.PfbFftSize.HasValue == true &&
                                       request.Hints?.PfbHopSize.HasValue == true &&
                                       request.Hints?.PfbFramesPerBatch.HasValue == true;
                var warnings = fullyForcedShape
                    ? Array.Empty<string>()
                    : new[]
                    {
                        "PFB K/H/FramesPerBatch was selected by deterministic feasibility policy; no benchmark profile was applied."
                    };
                return new PfbLayout(
                    candidate.FftSize,
                    candidate.HopSize,
                    candidate.FramesPerBatch,
                    requirements,
                    prototype,
                    fineStages,
                    warnings);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
            {
                lastFailure = exception;
            }
        }

        throw new ArgumentException(
            $"Every PFB candidate failed exact filter validation. Last failure: {lastFailure?.Message ?? "unknown"}",
            nameof(request),
            lastFailure);
    }

    internal static IReadOnlyList<(int FftSize, int HopSize, int FramesPerBatch)> InspectCandidates(
        ChannelizerRequest request)
    {
        var constraints = request.InputBlocks ?? new InputBlockConstraints();
        return EnumerateCandidates(request, constraints)
            .Select(candidate => (candidate.FftSize, candidate.HopSize, candidate.FramesPerBatch))
            .ToArray();
    }

    private static IEnumerable<Candidate> EnumerateCandidates(
        ChannelizerRequest request,
        InputBlockConstraints constraints)
    {
        var targetFftSize = TargetFftSize(request);
        var candidates = new List<Candidate>();
        foreach (var fftSize in EnumerateFftSizes(request, targetFftSize))
        {
            var hopCandidates = EnumerateHopSizes(request, constraints, fftSize).ToArray();
            foreach (var hopSize in hopCandidates)
            {
                foreach (var frames in EnumerateFrameCounts(request, constraints, hopSize))
                {
                    var chunk = checked(frames * hopSize);
                    var shapeDistance = Math.Abs(Math.Log2(fftSize / (double)targetFftSize));
                    var oversampling = fftSize / (double)hopSize;
                    var chunkDistance = Math.Abs((long)chunk - constraints.PreferredChunkSize) /
                                        (double)constraints.PreferredChunkSize;
                    var framePenalty = IsPowerOfTwo(frames) ? 0 : 0.25;
                    var score = (4 * shapeDistance) + oversampling + chunkDistance + framePenalty;
                    candidates.Add(new Candidate(fftSize, hopSize, frames, chunk, score));
                }
            }
        }

        return candidates
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => Math.Abs((long)candidate.ChunkSize - constraints.PreferredChunkSize))
            .ThenBy(candidate => candidate.FftSize)
            .ThenByDescending(candidate => candidate.HopSize)
            .ThenByDescending(candidate => candidate.FramesPerBatch);
    }

    private static IEnumerable<int> EnumerateFftSizes(ChannelizerRequest request, int targetFftSize)
    {
        if (request.Hints?.PfbFftSize is { } forced)
        {
            yield return forced;
            yield break;
        }

        var values = new HashSet<int>();
        for (var value = 2; value <= MaximumAutomaticFftSize; value <<= 1)
        {
            AddIfValid(values, value);
        }

        foreach (var value in values.OrderBy(value => Math.Abs(Math.Log2(value / (double)targetFftSize))))
        {
            yield return value;
        }
    }

    private static IEnumerable<int> EnumerateHopSizes(
        ChannelizerRequest request,
        InputBlockConstraints constraints,
        int fftSize)
    {
        if (request.Hints?.PfbHopSize is { } forced)
        {
            if (forced <= fftSize)
            {
                _ = PfbPrototypeDesign.Analyze(request, fftSize, forced);
                yield return forced;
            }

            yield break;
        }

        var count = 0;
        for (var hopSize = Math.Min(fftSize, constraints.MaxChunkSize);
             hopSize >= 1 && count < MaximumHopCandidatesPerFftSize;
             hopSize--)
        {
            var feasible = false;
            try
            {
                _ = PfbPrototypeDesign.Analyze(request, fftSize, hopSize);
                feasible = true;
            }
            catch (ArgumentException)
            {
                // Geometry-only rejection; smaller H increases the coarse rate.
            }

            if (feasible)
            {
                yield return hopSize;
                count++;
            }
        }
    }

    private static IEnumerable<int> EnumerateFrameCounts(
        ChannelizerRequest request,
        InputBlockConstraints constraints,
        int hopSize)
    {
        var maximumFrames = constraints.MaxChunkSize / hopSize;
        if (maximumFrames < 1)
        {
            yield break;
        }

        if (request.Hints?.PfbFramesPerBatch is { } forced)
        {
            if (forced <= maximumFrames)
            {
                yield return forced;
            }

            yield break;
        }

        var desired = Math.Clamp(
            (int)Math.Round(constraints.PreferredChunkSize / (double)hopSize, MidpointRounding.AwayFromZero),
            1,
            maximumFrames);
        var values = new HashSet<int> { desired, PreviousPowerOfTwo(desired), maximumFrames };
        var nextPower = NextPowerOfTwo(desired);
        if (nextPower <= maximumFrames)
        {
            values.Add(nextPower);
        }

        foreach (var value in values
                     .Where(value => value >= 1 && value <= maximumFrames)
                     .OrderBy(value => Math.Abs((long)value * hopSize - constraints.PreferredChunkSize))
                     .ThenByDescending(IsPowerOfTwo))
        {
            yield return value;
        }
    }

    private static int TargetFftSize(ChannelizerRequest request)
    {
        if (request.Hints?.PfbFftSize is { } forced)
        {
            return forced;
        }

        var widestOccupiedWidth = request.Channels.Max(channel => channel.PassbandWidthHz + channel.TransitionWidthHz);
        var desired = request.InputSampleRateHz / widestOccupiedWidth;
        var target = 2;
        while (target < desired && target < MaximumAutomaticFftSize)
        {
            target <<= 1;
        }

        return Math.Clamp(target, 2, MaximumAutomaticFftSize);
    }

    private static void AddIfValid(ISet<int> values, int value)
    {
        if (value is >= 2 and <= MaximumAutomaticFftSize)
        {
            values.Add(value);
        }
    }

    private static int PreviousPowerOfTwo(int value)
    {
        var result = 1;
        while (result <= value / 2)
        {
            result <<= 1;
        }

        return result;
    }

    private static int NextPowerOfTwo(int value)
    {
        var result = 1;
        while (result < value && result <= int.MaxValue / 2)
        {
            result <<= 1;
        }

        return result;
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
}
