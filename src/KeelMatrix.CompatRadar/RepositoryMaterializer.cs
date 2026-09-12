using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml;

namespace KeelMatrix.CompatRadar;

internal sealed class MaterializationScope : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] ExcludedSegments = [".git", "bin", "obj", ".vs", ".nuget", ".dotnet", "artifacts", "TestResults"];
    private readonly string root;

    private MaterializationScope(string root)
    {
        this.root = root;
    }

    public string Root => root;

    public static MaterializationScope Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "compat-radar", Guid.NewGuid().ToString("N"));
        if (!PathUtilities.IsWithinRoot(Path.GetTempPath(), root))
        {
            throw new InvalidOperationException("The temporary materialization directory escaped the system temporary root.");
        }

        Directory.CreateDirectory(root);
        return new MaterializationScope(root);
    }

    public string CreateComparisonDirectory(string findingId)
    {
        var directory = Path.Combine(root, "comparisons", Sanitize(findingId));
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static void CopyRepository(string source, string destination)
    {
        var sourceAttributes = File.GetAttributes(source);
        if ((sourceAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The repository root is a reparse point and cannot be materialized safely.");
        }

        Directory.CreateDirectory(destination);
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var relative = Path.GetRelativePath(source, entry);
                if (IsExcludedRelativePath(relative))
                {
                    continue;
                }

                var destinationPath = Path.Combine(destination, relative);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.CreateDirectory(destinationPath);
                    pending.Push(entry);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    File.Copy(entry, destinationPath, overwrite: true);
                }
            }
        }
    }

    /// <summary>
    /// True when a repository-relative path is outside the materialized comparison content
    /// (Git metadata, build output, local caches, and local artifacts).
    /// </summary>
    public static bool IsExcludedRelativePath(string relativePath)
    {
        var firstSegment = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return ExcludedSegments.Contains(firstSegment, StringComparer.Ordinal);
    }

    /// <summary>
    /// Computes a deterministic identity of exactly the content a comparison materializes,
    /// so a witness can identify the tested source state even when the working tree has
    /// uncommitted changes that Git metadata alone does not describe.
    /// </summary>
    public static string ComputeRepositoryContentHash(string repository)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in EnumerateMaterializedFiles(repository))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(repository, file).Replace(Path.DirectorySeparatorChar, '/')));
            hash.AppendData([0]);
            hash.AppendData(File.ReadAllBytes(file));
            hash.AppendData([0]);
        }

        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// True when Git reports local modifications or untracked files among the content that a
    /// comparison materializes. Repositories without usable Git metadata report false because
    /// their revision is already derived from the materialized content itself.
    /// </summary>
    public static bool HasUncommittedMaterializedChanges(string repository)
    {
        try
        {
            var gitDirectory = Path.Combine(repository, ".git");
            if (!Directory.Exists(gitDirectory) && !File.Exists(gitDirectory))
            {
                return false;
            }

            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = repository,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("status");
            process.StartInfo.ArgumentList.Add("--porcelain");
            process.StartInfo.ArgumentList.Add("--untracked-files=all");
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            if (process.ExitCode != 0)
            {
                return false;
            }

            return output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => NormalizeStatusPath(line.Length > 3 ? line[3..] : line))
                .Where(path => path.Length > 0)
                .Any(path => !IsExcludedRelativePath(path));
        }
        catch
        {
            // An unusable Git client cannot make the content identity less deterministic.
            return false;
        }
    }

    private static string NormalizeStatusPath(string statusPath)
    {
        var path = statusPath.Trim();
        var renameIndex = path.IndexOf(" -> ", StringComparison.Ordinal);
        if (renameIndex >= 0)
        {
            path = path[(renameIndex + 4)..];
        }

        path = path.Trim().Trim('"').Replace('\\', '/');
        return path.TrimStart('/');
    }

    private static IEnumerable<string> EnumerateMaterializedFiles(string repository)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(repository);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if (IsExcludedRelativePath(Path.GetRelativePath(repository, entry)))
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
        }

        return files.OrderBy(file => Path.GetRelativePath(repository, file), StringComparer.Ordinal);
    }

    public static CandidateOverrideResult ApplyPackageOverride(string repository, string package, string candidateVersion)
    {
        var changed = 0;
        foreach (var file in Directory.EnumerateFiles(repository, "*.*", SearchOption.AllDirectories)
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                         || file.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                         || file.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var document = XDocument.Load(file, LoadOptions.PreserveWhitespace);
                var touched = false;
                foreach (var element in document.Descendants().Where(element => element.Name.LocalName is "PackageReference" or "PackageVersion"))
                {
                    var include = (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update");
                    if (!string.Equals(include, package, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var versionAttribute = element.Attribute("Version") ?? element.Attribute("VersionOverride");
                    if (versionAttribute is null)
                    {
                        element.SetAttributeValue(element.Name.LocalName == "PackageVersion" ? "Version" : "VersionOverride", candidateVersion);
                    }
                    else
                    {
                        versionAttribute.Value = candidateVersion;
                    }

                    touched = true;
                    changed++;
                }

                if (touched)
                {
                    document.Save(file, SaveOptions.DisableFormatting);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                return new CandidateOverrideResult(false, changed, $"Candidate package metadata could not be read safely ({exception.GetType().Name}).");
            }
        }

        return changed > 0
            ? new CandidateOverrideResult(true, changed, null)
            : new CandidateOverrideResult(false, 0, $"Package '{package}' was not found in a PackageReference or PackageVersion declaration.");
    }

    public static CandidateOverrideResult ApplySdkOverride(string repository, string candidateVersion)
    {
        var globalJson = Path.Combine(repository, "global.json");
        try
        {
            Dictionary<string, object?> json;
            if (File.Exists(globalJson))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(globalJson));
                json = document.RootElement.ValueKind == JsonValueKind.Object
                    ? document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal)
                    : new Dictionary<string, object?>(StringComparer.Ordinal);
            }
            else
            {
                json = new Dictionary<string, object?>(StringComparer.Ordinal);
            }

            json["sdk"] = new Dictionary<string, object?>
            {
                ["version"] = candidateVersion,
                ["rollForward"] = "latestPatch",
                ["allowPrerelease"] = true
            };
            File.WriteAllText(globalJson, JsonSerializer.Serialize(json, JsonOptions));
            return new CandidateOverrideResult(true, 1, null);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new CandidateOverrideResult(false, 0, $"SDK candidate configuration could not be written safely ({exception.GetType().Name}).");
        }
    }

    public static FeedConfigurationResult AddCandidateFeed(string repository, string feed, string? watchedPackage = null)
    {
        try
        {
            var configPath = Directory.EnumerateFiles(repository, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), "NuGet.config", StringComparison.OrdinalIgnoreCase));
            configPath ??= Path.Combine(repository, "NuGet.config");

            XDocument document;
            if (File.Exists(configPath))
            {
                document = XDocument.Load(configPath, LoadOptions.PreserveWhitespace);
            }
            else
            {
                document = new XDocument(
                    new XElement("configuration",
                        new XElement("packageSources")));
            }

            var configuration = document.Element("configuration");
            if (configuration is null)
            {
                configuration = new XElement("configuration");
                document.Add(configuration);
            }

            var packageSources = configuration.Element("packageSources");
            if (packageSources is null)
            {
                packageSources = new XElement("packageSources");
                configuration.Add(packageSources);
            }

            var existing = packageSources.Elements("add")
                .FirstOrDefault(element => string.Equals((string?)element.Attribute("key"), "compat-radar-candidate", StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                packageSources.Add(new XElement("add", new XAttribute("key", "compat-radar-candidate"), new XAttribute("value", feed)));
            }
            else
            {
                existing.SetAttributeValue("value", feed);
            }

            // Preserve all repository sources and mappings while adding only the
            // watched package to the isolated candidate source.
            if (!string.IsNullOrWhiteSpace(watchedPackage))
            {
                var packageSourceMapping = configuration.Element("packageSourceMapping");
                if (packageSourceMapping is not null)
                {
                    var candidateMapping = packageSourceMapping.Elements("packageSource")
                        .FirstOrDefault(element => string.Equals(
                            (string?)element.Attribute("key"),
                            "compat-radar-candidate",
                            StringComparison.OrdinalIgnoreCase));
                    if (candidateMapping is null)
                    {
                        candidateMapping = new XElement(
                            "packageSource",
                            new XAttribute("key", "compat-radar-candidate"));
                        packageSourceMapping.Add(candidateMapping);
                    }

                    if (!candidateMapping.Elements("package")
                        .Any(element => string.Equals((string?)element.Attribute("pattern"), watchedPackage, StringComparison.OrdinalIgnoreCase)))
                    {
                        candidateMapping.Add(new XElement("package", new XAttribute("pattern", watchedPackage)));
                    }
                }
            }

            document.Save(configPath, SaveOptions.DisableFormatting);
            return new FeedConfigurationResult(true, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
        {
            return new FeedConfigurationResult(false, $"The candidate feed could not be added to the isolated NuGet configuration ({exception.GetType().Name}).");
        }
    }

    public static string ComputeRepositoryRevision(string repository)
    {
        try
        {
            var gitDirectory = Path.Combine(repository, ".git");
            if (Directory.Exists(gitDirectory) || File.Exists(gitDirectory))
            {
                using var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "git",
                        WorkingDirectory = repository,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                process.StartInfo.ArgumentList.Add("rev-parse");
                process.StartInfo.ArgumentList.Add("HEAD");
                process.Start();
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);
                if (process.ExitCode == 0 && output.Length is >= 7 and <= 64 && output.All(Uri.IsHexDigit))
                {
                    return output.ToLowerInvariant();
                }
            }
        }
        catch
        {
            // The content hash below is the safe fallback for repositories without usable Git metadata.
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in EnumerateMaterializedFiles(repository))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(repository, file).Replace(Path.DirectorySeparatorChar, '/')));
            hash.AppendData([0]);
            hash.AppendData(File.ReadAllBytes(file));
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string ComputeRepositoryIdentity(string repository, string revision)
    {
        try
        {
            var gitDirectory = Path.Combine(repository, ".git");
            if (Directory.Exists(gitDirectory) || File.Exists(gitDirectory))
            {
                using var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "git",
                        WorkingDirectory = repository,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                process.StartInfo.ArgumentList.Add("config");
                process.StartInfo.ArgumentList.Add("--get");
                process.StartInfo.ArgumentList.Add("remote.origin.url");
                process.Start();
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);
                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    return SanitizeRepositoryIdentity(output, revision);
                }
            }
        }
        catch
        {
            // A local identity remains useful when Git metadata is unavailable.
        }

        return $"local:{revision}";
    }

    private static string SanitizeRepositoryIdentity(string value, string revision)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "ssh")
        {
            // Remote URLs can contain credentials or query-string tokens. Keep
            // the stable scheme/host/path identity without copying those values into a report.
            return (uri.Scheme + "://" + uri.Host + uri.AbsolutePath).TrimEnd('/');
        }

        if (value.StartsWith("git@", StringComparison.Ordinal) || value.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return $"local:{revision}";
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Cleanup is best effort after process-tree termination.
        }
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_');
        }

        return builder.Length == 0 ? "comparison" : builder.ToString();
    }
}

internal sealed record CandidateOverrideResult(bool Applied, int ChangedFiles, string? Error);

internal sealed record FeedConfigurationResult(bool Applied, string? Error);
