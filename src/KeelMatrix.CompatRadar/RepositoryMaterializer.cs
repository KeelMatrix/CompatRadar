using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml;

namespace KeelMatrix.CompatRadar;

internal sealed class MaterializationScope : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
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
                var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (firstSegment is ".git" or "bin" or "obj" or ".vs" or ".nuget" or ".dotnet" or "artifacts" or "TestResults")
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

    public static FeedConfigurationResult AddCandidateFeed(string repository, string feed)
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
        foreach (var file in Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories)
                     .Where(file => !Path.GetRelativePath(repository, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         .Any(part => part is ".git" or "bin" or "obj" or ".nuget" or ".dotnet" or "artifacts"))
                     .OrderBy(file => Path.GetRelativePath(repository, file), StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(repository, file).Replace(Path.DirectorySeparatorChar, '/')));
            hash.AppendData([0]);
            hash.AppendData(File.ReadAllBytes(file));
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
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
