using System.Diagnostics;
using System.IO.Compression;

namespace KeelMatrix.CompatRadar.Tests;

public sealed class PackageContractTests
{
    [Fact]
    public void GeneratedPackagePassesExactInspection()
    {
        var packageDirectory = PackToTemporaryDirectory();
        try
        {
            var result = Inspect(packageDirectory);

            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            TestFixture.DeleteRepository(packageDirectory);
        }
    }

    [Fact]
    public void UnexpectedBenignLookingEntryFailsInspection()
    {
        var packageDirectory = PackToTemporaryDirectory();
        try
        {
            var packagePath = Path.Combine(packageDirectory, "KeelMatrix.CompatRadar.0.1.0.nupkg");
            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
            using (var writer = new StreamWriter(archive.CreateEntry("tools/net8.0/any/harmless.txt").Open()))
            {
                writer.Write("This file is intentionally not part of the package contract.");
            }

            var result = Inspect(packageDirectory);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Unexpected package entry", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestFixture.DeleteRepository(packageDirectory);
        }
    }

    [Fact]
    public void PackageReadmeLinkToUnpackedContentFailsInspection()
    {
        var packageDirectory = PackToTemporaryDirectory();
        try
        {
            var packagePath = Path.Combine(packageDirectory, "KeelMatrix.CompatRadar.0.1.0.nupkg");
            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
            {
                var readme = archive.GetEntry("README.md") ?? throw new InvalidOperationException("The packed README is missing.");
                string contents;
                using (var reader = new StreamReader(readme.Open()))
                {
                    contents = reader.ReadToEnd();
                }

                readme.Delete();
                using var writer = new StreamWriter(archive.CreateEntry("README.md").Open());
                writer.Write(contents + Environment.NewLine + "See [compatibility policy](docs/compatibility.md)." + Environment.NewLine);
            }

            var result = Inspect(packageDirectory);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("does not resolve to an entry in the package", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            TestFixture.DeleteRepository(packageDirectory);
        }
    }

    private static string PackToTemporaryDirectory()
    {
        var root = FindRepositoryRoot();
        var packageDirectory = Path.Combine(Path.GetTempPath(), "compat-radar-package-contract", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageDirectory);
        var repositoryCommit = GetRepositoryCommit(root);
        var result = Run(
            "dotnet",
            [
                "pack",
                Path.Combine("src", "KeelMatrix.CompatRadar", "KeelMatrix.CompatRadar.csproj"),
                "-c", "Release",
                "--no-restore",
                "--include-symbols",
                "-p:SymbolPackageFormat=snupkg",
                $"-p:RepositoryCommit={repositoryCommit}",
                "--output", packageDirectory
            ],
            root);
        if (result.ExitCode != 0)
        {
            TestFixture.DeleteRepository(packageDirectory);
            throw new InvalidOperationException($"dotnet pack failed: {result.Output}");
        }

        return packageDirectory;
    }

    private static string GetRepositoryCommit(string root)
    {
        var result = Run("git", ["rev-parse", "HEAD"], root);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Could not determine the repository commit: {result.Output}");
        }

        var commit = result.Output.Trim();
        if (commit.Length != 40 || !commit.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException($"Repository commit was not a full SHA-1: {commit}");
        }

        return commit;
    }

    private static (int ExitCode, string Output) Inspect(string packageDirectory)
    {
        var root = FindRepositoryRoot();
        return Run(
            "pwsh",
            [
                "-NoProfile", "-File", Path.Combine(root, "scripts", "inspect-package.ps1"),
                "-PackageDirectory", packageDirectory,
                "-ExpectedVersion", "0.1.0"
            ],
            root);
    }

    private static (int ExitCode, string Output) Run(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(standardOutput, standardError);
        return (process.ExitCode, standardOutput.Result + standardError.Result);
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
