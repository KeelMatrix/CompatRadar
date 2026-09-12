using System.Diagnostics;
using System.IO.Compression;

namespace KeelMatrix.CompatRadar.Tests;

public sealed class PackageContractTests
{
    private const string ApprovedDescription = "Test your .NET repository against future SDK/runtime and NuGet candidates, confirm real breakage, and localize the first bad candidate before normal upgrade time.";

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
            RewriteReadme(packagePath, "See [compatibility policy](docs/compatibility.md).");

            var result = Inspect(packageDirectory);

            Assert.NotEqual(0, result.ExitCode);
            WriteInspectionDiagnostics("package-readme-link-inspection.txt", packageDirectory, result);
            Assert.Contains("Package README link is unresolved", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            TestFixture.DeleteRepository(packageDirectory);
        }
    }

    [Theory]
    [InlineData("<copyright>KeelMatrix</copyright>", "<copyright></copyright>")]
    [InlineData("<copyright>KeelMatrix</copyright>", "<copyright>Copyright (c) 2026 KeelMatrix</copyright>")]
    [InlineData("<copyright>KeelMatrix</copyright>", "<copyright>keelmatrix</copyright>")]
    [InlineData("<copyright>KeelMatrix</copyright>", "<copyright>KEELMATRIX</copyright>")]
    public void MissingWrongOrCaseMismatchedCopyrightFailsInspection(string search, string replacement)
    {
        var packageDirectory = PackToTemporaryDirectory();
        try
        {
            RewriteNuspec(Path.Combine(packageDirectory, "KeelMatrix.CompatRadar.0.1.0.nupkg"), search, replacement);

            var result = Inspect(packageDirectory);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Copyright metadata mismatch", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            TestFixture.DeleteRepository(packageDirectory);
        }
    }

    [Fact]
    public void DriftedDescriptionFailsInspection()
    {
        var packageDirectory = PackToTemporaryDirectory();
        try
        {
            RewriteNuspec(
                Path.Combine(packageDirectory, "KeelMatrix.CompatRadar.0.1.0.nupkg"),
                ApprovedDescription,
                "Test your .NET repository against future SDK and NuGet candidates, confirm real breakage, and localize the first bad candidate before normal upgrade time.");

            var result = Inspect(packageDirectory);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Description metadata mismatch", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            TestFixture.DeleteRepository(packageDirectory);
        }
    }

    [Fact]
    public void PackedMetadataMatchesTheApprovedCopyrightAndDescription()
    {
        var packageDirectory = PackToTemporaryDirectory();
        try
        {
            var packagePath = Path.Combine(packageDirectory, "KeelMatrix.CompatRadar.0.1.0.nupkg");
            using var archive = ZipFile.OpenRead(packagePath);
            var nuspec = archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            using var reader = new StreamReader(nuspec.Open());
            var xml = System.Xml.Linq.XDocument.Parse(reader.ReadToEnd());
            var metadata = xml.Root!.Elements().Single(element => element.Name.LocalName == "metadata");
            var copyright = metadata.Elements().Single(element => element.Name.LocalName == "copyright").Value;
            var description = metadata.Elements().Single(element => element.Name.LocalName == "description").Value;

            Assert.Equal("KeelMatrix", copyright);
            Assert.Equal(ApprovedDescription, description);
        }
        finally
        {
            TestFixture.DeleteRepository(packageDirectory);
        }
    }

    private static void WriteInspectionDiagnostics(string fileName, string packageDirectory, (int ExitCode, string Output) result)
    {
        var directory = Path.Combine(FindRepositoryRoot(), "artifacts", "test-results");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, fileName),
            $"exit code: {result.ExitCode}{Environment.NewLine}{Environment.NewLine}package directory: {packageDirectory}{Environment.NewLine}{Environment.NewLine}{result.Output}");
    }

    /// <summary>
    /// Rewrites nuspec metadata by rebuilding the archive so a negative package-contract test can
    /// prove that inspection fails for missing, wrong, or case-mismatched values.
    /// </summary>
    private static void RewriteNuspec(string packagePath, string search, string replacement)
    {
        var staged = packagePath + ".staged";
        using (var source = ZipFile.OpenRead(packagePath))
        using (var target = ZipFile.Open(staged, ZipArchiveMode.Create))
        {
            var nuspec = source.Entries.FirstOrDefault(entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The package nuspec is missing.");
            foreach (var entry in source.Entries)
            {
                var created = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var input = entry.Open();
                using var output = created.Open();
                if (string.Equals(entry.FullName, nuspec.FullName, StringComparison.Ordinal))
                {
                    using var reader = new StreamReader(input);
                    var text = reader.ReadToEnd();
                    if (!text.Contains(search, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"The nuspec does not contain '{search}'.");
                    }

                    using var writer = new StreamWriter(output);
                    writer.Write(text.Replace(search, replacement, StringComparison.Ordinal));
                }
                else
                {
                    input.CopyTo(output);
                }
            }
        }

        File.Move(staged, packagePath, overwrite: true);
    }

    /// <summary>
    /// Rewrites the packed README by rebuilding the archive, which behaves the same on every
    /// platform (update-mode archive editing does not).
    /// </summary>
    private static void RewriteReadme(string packagePath, string appendedMarkdown)
    {
        var staged = packagePath + ".staged";
        using (var source = ZipFile.OpenRead(packagePath))
        using (var target = ZipFile.Open(staged, ZipArchiveMode.Create))
        {
            if (source.GetEntry("README.md") is null) throw new InvalidOperationException("The packed README is missing.");
            foreach (var entry in source.Entries)
            {
                var created = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var input = entry.Open();
                using var output = created.Open();
                if (string.Equals(entry.FullName, "README.md", StringComparison.Ordinal))
                {
                    using var reader = new StreamReader(input);
                    using var writer = new StreamWriter(output);
                    writer.Write(reader.ReadToEnd());
                    writer.Write(Environment.NewLine);
                    writer.Write(appendedMarkdown);
                    writer.Write(Environment.NewLine);
                }
                else
                {
                    input.CopyTo(output);
                }
            }
        }

        File.Move(staged, packagePath, overwrite: true);
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
