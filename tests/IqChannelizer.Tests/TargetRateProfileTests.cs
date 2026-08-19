using System.Text.Json;

namespace IqChannelizer.Tests;

public sealed class TargetRateProfileTests
{
    [Test]
    public void StoredTargetProfileRecordsRealtimeResultWithoutAllocations()
    {
        var path = FindRepositoryFile("artifacts", "benchmarks", "target-100ms-profile.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var results = root.GetProperty("Results").EnumerateArray().ToArray();
        var pfb = results.Single(result => result.GetProperty("Strategy").GetString() == "Pfb");

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("SchemaVersion").GetInt32(), Is.EqualTo(1));
            Assert.That(root.GetProperty("Configuration").GetProperty("InputSampleRateHz").GetDouble(), Is.EqualTo(100_000_000));
            Assert.That(root.GetProperty("Configuration").GetProperty("Iterations").GetInt32(), Is.EqualTo(500));
            Assert.That(root.GetProperty("AnyStrategyMeetsTarget").GetBoolean(), Is.True);
            Assert.That(results, Has.Length.EqualTo(2));
            Assert.That(results, Has.All.Matches<JsonElement>(result =>
                result.GetProperty("ManagedAllocatedBytes").GetInt64() == 0));
            Assert.That(pfb.GetProperty("SelectedSimdBackend").GetString(), Is.EqualTo("Avx512"));
            Assert.That(pfb.GetProperty("MeetsTargetRate").GetBoolean(), Is.True);
            Assert.That(pfb.GetProperty("RealtimeMarginAt100MegaSamplesPerSecond").GetDouble(), Is.GreaterThan(1));
        });
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
