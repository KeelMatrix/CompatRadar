namespace KeelMatrix.CompatRadar.Tests;

public sealed class ReleaseContractTests
{
    [Fact]
    public void FirstReleaseWorkflowIsExactAndFailClosed()
    {
        var workflow = File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "release.yml"));
        var publishStart = workflow.IndexOf("  publish:", StringComparison.Ordinal);
        Assert.True(publishStart > 0, "The release workflow must have a separate publication job.");
        var validation = workflow[..publishStart];
        var publication = workflow[publishStart..];

        Assert.Contains("- 'v0.1.0'", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("- 'v*'", workflow, StringComparison.Ordinal);
        Assert.Contains("$GITHUB_REF_NAME\" != 'v0.1.0'", validation, StringComparison.Ordinal);
        Assert.DoesNotContain("vX.Y.Z", workflow, StringComparison.Ordinal);

        Assert.Equal(1, Count(workflow, "id-token: write"));
        Assert.DoesNotContain("id-token: write", validation, StringComparison.Ordinal);
        Assert.Contains("needs: validate", publication, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/checkout", publication, StringComparison.Ordinal);
        foreach (var sourceOperation in new[] { "dotnet restore", "dotnet build", "dotnet test", "dotnet pack", "dotnet format" })
        {
            Assert.DoesNotContain(sourceOperation, publication, StringComparison.Ordinal);
        }

        Assert.Contains("scripts/security-audit.ps1", validation, StringComparison.Ordinal);
        var changelogValidation = validation.IndexOf("scripts/Test-ChangelogContract.ps1", StringComparison.Ordinal);
        Assert.True(changelogValidation >= 0, "The release validation job must run the repository changelog contract.");
        Assert.True(changelogValidation < validation.IndexOf("dotnet pack", StringComparison.Ordinal), "The changelog contract must run before packing.");
        Assert.Contains("-ExpectedPackageVersion $env:RELEASE_VERSION", validation, StringComparison.Ordinal);
        Assert.Contains("-ExpectedCommit $env:GITHUB_SHA", validation, StringComparison.Ordinal);
        Assert.Contains("scripts/inspect-package.ps1", validation, StringComparison.Ordinal);
        Assert.Contains("smoke/package-consumer-smoke.ps1", validation, StringComparison.Ordinal);
        Assert.Contains("NuGet/login@v1", publication, StringComparison.Ordinal);
        Assert.Contains("user: dmitriyzen", publication, StringComparison.Ordinal);
        Assert.DoesNotContain("--skip-duplicate", workflow, StringComparison.Ordinal);
        Assert.Equal(1, Count(publication, "dotnet nuget push $nupkg"));
        Assert.Equal(1, Count(publication, "dotnet nuget push $snupkg"));
        Assert.Contains("--no-symbols", publication, StringComparison.Ordinal);
        Assert.Equal(1, Count(validation, "KeelMatrix.CompatRadar.0.1.0.nupkg"));
        Assert.Equal(1, Count(validation, "KeelMatrix.CompatRadar.0.1.0.snupkg"));
        Assert.Equal(1, Count(publication, "KeelMatrix.CompatRadar.0.1.0.nupkg"));
        Assert.Equal(1, Count(publication, "KeelMatrix.CompatRadar.0.1.0.snupkg"));
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length) count++;
        return count;
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
