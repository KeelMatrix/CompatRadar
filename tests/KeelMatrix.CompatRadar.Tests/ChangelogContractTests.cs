using System.Diagnostics;
using System.Globalization;

namespace KeelMatrix.CompatRadar.Tests;

public sealed class ChangelogContractTests
{
    [Fact]
    public void PlannedTargetVersionCannotPassPublicationGate()
    {
        var fixture = CreateFixture("## 1.2.3 - Unreleased", "1.2.3", "1.2.3");
        try
        {
            var result = RunContract(fixture.Root, "1.2.3", fixture.Commit, "1.2.3");

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("pre-release", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestFixture.DeleteRepository(fixture.Root);
        }
    }

    [Fact]
    public void FinalizedTargetVersionPassesWhenMetadataIsConsistent()
    {
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var fixture = CreateFixture($"## 1.2.3 - {date}", "1.2.3", "1.2.3");
        try
        {
            var result = RunContract(fixture.Root, "1.2.3", fixture.Commit, "1.2.3");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Changelog contract passed", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            TestFixture.DeleteRepository(fixture.Root);
        }
    }

    [Fact]
    public void VersionMismatchFailsClosed()
    {
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var fixture = CreateFixture($"## 1.2.3 - {date}", "1.2.4", "1.2.4");
        try
        {
            var result = RunContract(fixture.Root, "1.2.3", fixture.Commit, "1.2.4");

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("does not match release version", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestFixture.DeleteRepository(fixture.Root);
        }
    }

    [Fact]
    public void InstallExampleVersionMismatchFailsClosed()
    {
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var fixture = CreateFixture($"## 1.2.3 - {date}", "1.2.3", "1.2.4");
        try
        {
            var result = RunContract(fixture.Root, "1.2.3", fixture.Commit, "1.2.3");

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("install example", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestFixture.DeleteRepository(fixture.Root);
        }
    }

    [Fact]
    public void ContractBindsToTheExactCheckedOutCommit()
    {
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var fixture = CreateFixture($"## 1.2.3 - {date}", "1.2.3", "1.2.3");
        try
        {
            var result = RunContract(fixture.Root, "1.2.3", new string('0', 40), "1.2.3");

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("checked-out commit", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestFixture.DeleteRepository(fixture.Root);
        }
    }

    private static ContractFixture CreateFixture(string changelogHeading, string packageVersion, string installVersion)
    {
        var root = Path.Combine(Path.GetTempPath(), "compat-radar-changelog-contract", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "CHANGELOG.md"), $"# Changelog\n\n{changelogHeading}\n\n- Initial release.\n");
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), $"<Project><PropertyGroup><CompatRadarReleaseVersion>{packageVersion}</CompatRadarReleaseVersion></PropertyGroup></Project>\n");
        File.WriteAllText(Path.Combine(root, "README.md"), $"# CompatRadar\n\n`dotnet tool install --global KeelMatrix.CompatRadar --version {installVersion}`\n");

        RunProcess("git", root, "init", "--quiet");
        RunProcess("git", root, "config", "user.name", "KeelMatrix");
        RunProcess("git", root, "config", "user.email", "keelmatrix@gmail.com");
        RunProcess("git", root, "add", "CHANGELOG.md", "Directory.Build.props", "README.md");
        RunProcess("git", root, "commit", "--quiet", "-m", "Create changelog contract fixture");
        var commit = RunProcess("git", root, "rev-parse", "HEAD").Output.Trim();
        return new ContractFixture(root, commit);
    }

    private static ProcessResult RunContract(string root, string expectedVersion, string expectedCommit, string expectedPackageVersion)
    {
        var script = Path.Combine(FindRepositoryRoot(), "scripts", "Test-ChangelogContract.ps1");
        return RunProcess(
            "pwsh",
            FindRepositoryRoot(),
            "-NoProfile",
            "-File",
            script,
            "-RepositoryPath",
            root,
            "-ChangelogPath",
            "CHANGELOG.md",
            "-ExpectedVersion",
            expectedVersion,
            "-ExpectedPackageVersion",
            expectedPackageVersion,
            "-ExpectedCommit",
            expectedCommit);
    }

    private static ProcessResult RunProcess(string fileName, string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output + error);
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

    private readonly record struct ContractFixture(string Root, string Commit);
    private readonly record struct ProcessResult(int ExitCode, string Output);
}
