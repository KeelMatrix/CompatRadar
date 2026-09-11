using System.Diagnostics;
using System.Text.Json;

namespace KeelMatrix.CompatRadar.Tests;

public sealed class SecurityAuditTests
{
    [Fact]
    public void ValidCleanReportPasses()
    {
        var result = RunAudit("Write-Output '{\"version\":1,\"parameters\":\"--vulnerable --include-transitive\",\"sources\":[\"https://api.nuget.org/v3/index.json\"],\"projects\":[{\"path\":\"app.csproj\"}]}'; exit 0");

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void VulnerableReportFails()
    {
        var result = RunAudit("Write-Output '{\"version\":1,\"parameters\":\"--vulnerable --include-transitive\",\"sources\":[\"https://api.nuget.org/v3/index.json\"],\"projects\":[{\"path\":\"app.csproj\",\"frameworks\":[{\"topLevelPackages\":[{\"id\":\"Bad.Package\",\"resolvedVersion\":\"1.0.0\",\"vulnerabilities\":[{\"severity\":\"high\"}]}]}]}]}'; exit 0");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("vulnerability", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AdvisoryUnavailableFailsClosedAsStatusUnavailable()
    {
        var result = RunAudit("Write-Error 'NU1900: vulnerability data could not be retrieved from the advisory service'; exit 1");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("status unavailable", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedReportFailsClosed()
    {
        var result = RunAudit("Write-Output 'not-json'; exit 0");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("unrecognized", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyReportFailsClosed()
    {
        var result = RunAudit("exit 0");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("empty", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommandFailureFailsClosed()
    {
        var result = RunAudit("Write-Output 'ordinary command failure'; exit 7");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("command failure", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private static (int ExitCode, string Output) RunAudit(string fixtureBody)
    {
        var root = FindRepositoryRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "compat-radar-security-audit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var fixture = Path.Combine(tempRoot, "audit-fixture.ps1");
        File.WriteAllText(fixture, fixtureBody);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-File", Path.Combine(root, "scripts", "security-audit.ps1"),
                         "-CommandPath", "pwsh", "-CommandArgumentsJson",
                         JsonSerializer.Serialize(new[] { "-NoProfile", "-File", fixture })
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start PowerShell.");
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
        finally
        {
            TestFixture.DeleteRepository(tempRoot);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "security-audit.ps1"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
