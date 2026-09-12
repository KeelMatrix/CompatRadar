using System.Text.Json;

namespace KeelMatrix.CompatRadar.Tests;

public sealed class ValidationCorpusContractTests
{
    private static readonly string[] PreviewCandidateVersions =
    [
        "11.0.100-preview.7.26381.103",
        "11.0.100-rc.1.26425.128"
    ];

    [Fact]
    public void ValidationCorpusManifestPinsDiverseRepositoriesCandidatesAndExpectedOutcomes()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "scripts", "validation-corpus.json")));
        var manifest = document.RootElement;

        Assert.Equal(1, manifest.GetProperty("schemaVersion").GetInt32());
        var repositories = manifest.GetProperty("repositories").EnumerateArray().ToArray();
        Assert.True(repositories.Length >= 5);
        Assert.Contains(repositories, repository => repository.GetProperty("name").GetString() == "RichardSzalay.MockHttp");
        Assert.Contains(repositories, repository => repository.GetProperty("name").GetString() == "WireMock.Net");
        Assert.Contains(repositories, repository => repository.GetProperty("name").GetString() == "Verify");
        Assert.All(repositories, repository =>
        {
            Assert.Matches("^[0-9a-f]{40}$", repository.GetProperty("ref").GetString()!);
            Assert.False(string.IsNullOrWhiteSpace(repository.GetProperty("complexityProfile").GetString()));
        });

        var candidates = manifest.GetProperty("previewCandidates").EnumerateArray().ToArray();
        Assert.True(candidates.Length >= 2);
        Assert.All(candidates, candidate =>
        {
            Assert.Contains(candidate.GetProperty("version").GetString()!, PreviewCandidateVersions);
            Assert.False(string.IsNullOrWhiteSpace(candidate.GetProperty("runtimeVersion").GetString()));
        });

        var expectedOutcomes = manifest.GetProperty("expectedOutcomes").EnumerateArray().ToArray();
        Assert.Contains(expectedOutcomes, outcome => outcome.GetProperty("group").GetString() == "incompatible" && outcome.GetProperty("expectedClassification").GetString() == "FUTURE_REGRESSION");
        Assert.Contains(expectedOutcomes, outcome => outcome.GetProperty("group").GetString() == "equivalent" && outcome.GetProperty("expectedClassification").GetString() == "COMPATIBLE");
        Assert.All(expectedOutcomes, outcome => Assert.False(string.IsNullOrWhiteSpace(outcome.GetProperty("case").GetString())));
        Assert.Contains(expectedOutcomes, outcome => outcome.GetProperty("case").GetString() == "sdk-preview-incompatible-candidate");
        Assert.Contains(expectedOutcomes, outcome => outcome.GetProperty("case").GetString() == "runtime-preview-incompatible-candidate");

        var requiredEvidence = manifest.GetProperty("requiredEvidence").EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[] { "incompatible-candidate-detection", "equivalent-state-false-regression-check", "stable-control-confirmation", "flaky-classification", "monotonic-and-non-monotonic-handling", "additive-feed-behavior", "unsupported-preview-behavior", "runtime", "restore-cost", "cleanup", "witness-samples", "witness-pack-structural-validation" })
        {
            Assert.Contains(required, requiredEvidence);
        }
    }

    [Fact]
    public void ValidationCorpusEvidenceIsGeneratedOnlyByTheCurrentRun()
    {
        var root = FindRepositoryRoot();
        Assert.False(File.Exists(Path.Combine(root, "docs", "validation-corpus.md.json")));
        Assert.False(File.Exists(Path.Combine(root, "docs", "validation-corpus.json")));
        var documentation = File.ReadAllText(Path.Combine(root, "docs", "validation-corpus.md"));
        Assert.Contains("generated", documentation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scripts/validation-corpus.json", documentation, StringComparison.Ordinal);

        var corpus = File.ReadAllText(Path.Combine(root, "scripts", "validation-corpus.ps1"));
        Assert.Contains("Read-ObservedOutcomes", corpus, StringComparison.Ordinal);
        Assert.DoesNotContain("detectionRatePercent = 100", corpus, StringComparison.Ordinal);
        Assert.DoesNotContain("incompatibleStatesDetected = 6", corpus, StringComparison.Ordinal);
        Assert.Contains("evaluationScope", corpus, StringComparison.Ordinal);
    }

    [Fact]
    public void WitnessPackValidationChecksStructureOnly()
    {
        var root = FindRepositoryRoot();
        var validator = File.ReadAllText(Path.Combine(root, "scripts", "validate-witness-pack.ps1"));

        Assert.Contains("structurallyComplete", validator, StringComparison.Ordinal);
        Assert.Contains("evaluationScope", validator, StringComparison.Ordinal);
        Assert.DoesNotContain("outcome = 'PASS'", validator, StringComparison.Ordinal);
        Assert.DoesNotContain("expectedClassification", validator.Split("$forbiddenProperties")[0], StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KeelMatrix.CompatRadar.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
