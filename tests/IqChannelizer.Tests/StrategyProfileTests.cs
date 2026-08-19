using System.Runtime.InteropServices;
using System.Text.Json;
using IqChannelizer.Abstractions;
using IqChannelizer.Runtime;

namespace IqChannelizer.Tests;

public sealed class StrategyProfileTests
{
    private static readonly StrategyProfileEnvironment AcceptedEnvironment = new(
        IsWindows: true,
        ProcessArchitecture: Architecture.X64,
        RuntimeMajorVersion: 10,
        ProcessorIdentifier: "AMD64 Family 25 Model 120 Stepping 0, AuthenticAMD",
        FftwVersion: "fftw-3.3.5-sse2-avx");

    [TestCase(1)]
    [TestCase(8)]
    [TestCase(32)]
    public void MatchingVersionedProfileSelectsFdcAndExplainsDecision(int channelCount)
    {
        var selection = StrategyProfileSelector.Resolve(Request(channelCount), AcceptedEnvironment);

        Assert.Multiple(() =>
        {
            Assert.That(selection.Strategy, Is.EqualTo(ChannelizerStrategy.Fdc));
            Assert.That(selection.ProfileKey, Is.EqualTo(StrategyProfileSelector.ProfileKey));
            Assert.That(selection.Explanation, Does.Contain($"Q={channelCount}"));
            Assert.That(selection.Explanation, Does.Contain("at least 2.5x faster"));
        });
    }

    [Test]
    public void ProfileRejectsUnknownEnvironmentAndRequestShape()
    {
        var wrongCpu = AcceptedEnvironment with { ProcessorIdentifier = "another CPU" };
        var wrongShape = Request(8) with { InputSampleRateHz = 2_000_000 };

        Assert.Multiple(() =>
        {
            Assert.That(
                () => StrategyProfileSelector.Resolve(Request(8), wrongCpu),
                Throws.TypeOf<NotSupportedException>().With.Message.Contains(StrategyProfileSelector.ProfileKey));
            Assert.That(
                () => StrategyProfileSelector.Resolve(wrongShape, AcceptedEnvironment),
                Throws.TypeOf<NotSupportedException>().With.Message.Contains(StrategyProfileSelector.ProfileKey));
        });
    }

    [Test]
    public void StoredProfileMatchesRuntimeKeyAndDecisionMargin()
    {
        var path = FindRepositoryFile("artifacts", "benchmarks", "strategy-profile-v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("schemaVersion").GetInt32(), Is.EqualTo(1));
            Assert.That(root.GetProperty("profileKey").GetString(), Is.EqualTo(StrategyProfileSelector.ProfileKey));
            Assert.That(root.GetProperty("decision").GetProperty("strategy").GetString(), Is.EqualTo("Fdc"));
            foreach (var result in root.GetProperty("nanosecondsPerInputSample").EnumerateArray())
            {
                var fdc = result.GetProperty("fdc").GetDouble();
                var pfb = result.GetProperty("pfb").GetDouble();
                Assert.That(pfb / fdc, Is.GreaterThanOrEqualTo(2.5));
            }
        });
    }

    [Test]
    public void FactoryEitherAppliesCurrentProfileOrRejectsItActionably()
    {
        var request = Request(8);
        try
        {
            _ = StrategyProfileSelector.Resolve(request, StrategyProfileSelector.CurrentEnvironment());
        }
        catch (NotSupportedException)
        {
            Assert.That(
                () => ChannelizerFactory.Create(request),
                Throws.TypeOf<NotSupportedException>().With.Message.Contains(StrategyProfileSelector.ProfileKey));
            return;
        }

        using var engine = ChannelizerFactory.Create(request);
        Assert.Multiple(() =>
        {
            Assert.That(engine.Plan.Strategy, Is.EqualTo(ChannelizerStrategy.Fdc));
            Assert.That(engine.Plan.BenchmarkProfileKey, Is.EqualTo(StrategyProfileSelector.ProfileKey));
            Assert.That(engine.Plan.Warnings, Has.Some.Contains("selected FDC"));
        });
    }

    private static ChannelizerRequest Request(int channelCount)
    {
        var channels = Enumerable.Range(0, channelCount)
            .Select(index => new ChannelRequest(
                index + 100,
                (index - (channelCount / 2)) * 15_625,
                10_000,
                10_000,
                60,
                0.2))
            .ToArray();
        return new ChannelizerRequest(
            1_000_000,
            channels,
            ChannelizerStrategy.Auto,
            new InputBlockConstraints(4096, 4096));
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativeParts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file '{Path.Combine(relativeParts)}'.");
    }
}
