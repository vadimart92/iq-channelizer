using System.Runtime.InteropServices;
using IqChannelizer.Abstractions;
using IqChannelizer.Fftw;

namespace IqChannelizer.Runtime;

internal sealed record StrategySelection(
    ChannelizerStrategy Strategy,
    string ProfileKey,
    string Explanation);

internal readonly record struct StrategyProfileEnvironment(
    bool IsWindows,
    Architecture ProcessArchitecture,
    int RuntimeMajorVersion,
    string ProcessorIdentifier,
    string FftwVersion);

internal static class StrategyProfileSelector
{
    public const string ProfileKey = "equal-spec-1m-10k-q1-8-32-v1";

    private const string ProfileProcessor = "AMD64 Family 25 Model 120 Stepping 0, AuthenticAMD";
    private const string ProfileFftwVersion = "3.3.5";

    public static StrategySelection Resolve(ChannelizerRequest request) =>
        Resolve(request, CurrentEnvironment());

    internal static StrategySelection Resolve(
        ChannelizerRequest request,
        StrategyProfileEnvironment environment)
    {
        if (!EnvironmentMatches(environment) || !RequestMatches(request))
        {
            throw new NotSupportedException(
                $"Auto strategy has no matching versioned benchmark profile. Available profile '{ProfileKey}' " +
                "requires Windows x64, .NET 10, FFTW 3.3.5, the recorded AMD Family 25 Model 120 CPU, " +
                "Fs=1,000,000 Hz, Q=1/8/32, the recorded 10 kHz passband/transition channel grid, " +
                "an exact 4096-sample block, no output-rate constraints, no forced strategy shapes, " +
                "and a Conservative PFB prototype.");
        }

        return new StrategySelection(
            ChannelizerStrategy.Fdc,
            ProfileKey,
            $"Strategy Auto selected FDC from benchmark profile '{ProfileKey}': FDC was at least 2.5x faster " +
            $"than PFB for Q={request.Channels.Count} across every measured scalar/AVX2/AVX-512 backend.");
    }

    internal static StrategyProfileEnvironment CurrentEnvironment() => new(
        OperatingSystem.IsWindows(),
        RuntimeInformation.ProcessArchitecture,
        Environment.Version.Major,
        Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? string.Empty,
        FftwRuntime.Info.Version);

    private static bool EnvironmentMatches(StrategyProfileEnvironment environment) =>
        environment.IsWindows &&
        environment.ProcessArchitecture == Architecture.X64 &&
        environment.RuntimeMajorVersion == 10 &&
        environment.ProcessorIdentifier.Equals(ProfileProcessor, StringComparison.OrdinalIgnoreCase) &&
        environment.FftwVersion.Contains(ProfileFftwVersion, StringComparison.Ordinal);

    private static bool RequestMatches(ChannelizerRequest request)
    {
        if (request.InputSampleRateHz != 1_000_000 ||
            request.Channels.Count is not (1 or 8 or 32) ||
            request.InputBlocks is not { PreferredChunkSize: 4096, MaxChunkSize: 4096 })
        {
            return false;
        }

        var hints = request.Hints;
        if (hints?.FdcDecimationFactor is not null ||
            hints?.PfbFftSize is not null ||
            hints?.PfbHopSize is not null ||
            hints?.PfbFramesPerBatch is not null ||
            hints?.PfbPrototypeDesign == PfbPrototypeDesignMode.FoldAware)
        {
            return false;
        }

        for (var index = 0; index < request.Channels.Count; index++)
        {
            var channel = request.Channels[index];
            var expectedCenter = (index - (request.Channels.Count / 2)) * 15_625d;
            if (channel.CenterFrequencyHz != expectedCenter ||
                channel.PassbandWidthHz != 10_000 ||
                channel.TransitionWidthHz != 10_000 ||
                channel.StopbandAttenuationDb != 60 ||
                channel.PassbandRippleDb != 0.2 ||
                channel.MinimumOutputSampleRateHz is not null ||
                channel.PreferredOutputSampleRateHz is not null)
            {
                return false;
            }
        }

        return true;
    }
}
