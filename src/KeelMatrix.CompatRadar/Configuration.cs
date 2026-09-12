using System.Text.Json;

namespace KeelMatrix.CompatRadar;

internal sealed record ConfigurationLoadResult(RadarConfiguration? Configuration, IReadOnlyList<string> Errors)
{
    public bool IsValid => Configuration is not null && Errors.Count == 0;
}

internal static class ConfigurationLoader
{
    private static readonly HashSet<string> RootProperties = ["version", "control", "watch", "validation", "policy"];
    private static readonly HashSet<string> ControlProperties = ["sdk"];
    private static readonly HashSet<string> WatchProperties = ["id", "kind", "package", "candidates", "feed"];
    private static readonly HashSet<string> ValidationProperties = ["command", "workingDirectory", "timeoutSeconds"];
    private static readonly HashSet<string> PolicyProperties = ["confirmationRuns"];

    public static ConfigurationLoadResult Load(string repositoryRoot, string relativePath)
    {
        var errors = new List<string>();
        string fullPath;
        try
        {
            fullPath = PathUtilities.ResolveUnderRoot(repositoryRoot, relativePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return new(null, [$"Configuration path is invalid: {exception.GetType().Name}."]);
        }

        if (!PathUtilities.IsWithinRoot(repositoryRoot, fullPath))
        {
            return new(null, ["Configuration path must stay inside the repository."]);
        }

        if (!File.Exists(fullPath))
        {
            return new(null, [$"Configuration file was not found: {PathUtilities.ToRepositoryRelative(repositoryRoot, fullPath)}."]);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(fullPath));
            var configuration = Parse(document.RootElement, repositoryRoot, PathUtilities.ToRepositoryRelative(repositoryRoot, fullPath), errors);
            return errors.Count == 0 ? new(configuration, []) : new(null, errors);
        }
        catch (JsonException)
        {
            return new(null, ["Configuration is not valid JSON."]);
        }
        catch (IOException)
        {
            return new(null, ["Configuration could not be read."]);
        }
        catch (UnauthorizedAccessException)
        {
            return new(null, ["Configuration could not be read due to access restrictions."]);
        }
    }

    private static RadarConfiguration? Parse(JsonElement root, string repositoryRoot, string repositoryRelativePath, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Configuration root must be an object.");
            return null;
        }

        RejectUnknown(root, RootProperties, "root", errors);
        var version = ReadRequiredInt(root, "version", "root", errors);
        if (version != RadarContract.SchemaVersion)
        {
            errors.Add($"Configuration version must be {RadarContract.SchemaVersion}.");
        }

        var control = ParseControl(root, errors);
        var watches = ParseWatches(root, errors);
        var validation = ParseValidation(root, errors);
        var policy = ParsePolicy(root, errors);
        return control is null || watches is null || validation is null || policy is null
            ? null
            : new RadarConfiguration(version ?? 0, control, watches, validation, policy, repositoryRelativePath);
    }

    private static ControlConfiguration? ParseControl(JsonElement root, List<string> errors)
    {
        if (!TryGetRequiredObject(root, "control", "root", errors, out var element))
        {
            return null;
        }

        RejectUnknown(element, ControlProperties, "control", errors);
        var sdk = ReadRequiredString(element, "sdk", "control", errors);
        if (sdk is not null && !sdk.Equals("current", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("control.sdk must be 'current'; an explicit stable SDK is not supported in v1.");
        }

        return sdk is null ? null : new ControlConfiguration("current");
    }

    private static List<WatchConfiguration>? ParseWatches(JsonElement root, List<string> errors)
    {
        if (!TryGetRequiredArray(root, "watch", "root", errors, out var array) || array.GetArrayLength() == 0)
        {
            if (array.ValueKind == JsonValueKind.Array && array.GetArrayLength() == 0)
            {
                errors.Add("watch must contain at least one candidate channel.");
            }

            return null;
        }

        var watches = new List<WatchConfiguration>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            index++;
            if (element.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"watch[{index}] must be an object.");
                continue;
            }

            RejectUnknown(element, WatchProperties, $"watch[{index}]", errors);
            var id = ReadOptionalString(element, "id", $"watch[{index}]", errors) ?? $"watch-{index:000}";
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || !ids.Add(id))
            {
                errors.Add($"watch[{index}].id must be unique and contain 1-64 characters.");
            }

            var kindText = ReadRequiredString(element, "kind", $"watch[{index}]", errors);
            var kind = ParseWatchKind(kindText, index, errors);
            var candidates = ReadCandidateArray(element, index, errors);
            var package = ReadOptionalString(element, "package", $"watch[{index}]", errors);
            var feed = ReadOptionalString(element, "feed", $"watch[{index}]", errors);

            if (feed is not null && !IsSafeFeed(feed))
            {
                errors.Add($"watch[{index}].feed must be an absolute HTTP(S) URL with no user-info, query string, or fragment; feed credentials are not supported in configuration v1.");
            }

            if (kind == WatchKind.NuGetPrerelease)
            {
                if (string.IsNullOrWhiteSpace(package))
                {
                    errors.Add($"watch[{index}].package is required for nuget-prerelease.");
                }

            }
            else if (package is not null || feed is not null)
            {
                errors.Add($"watch[{index}] SDK/runtime previews cannot specify package or feed.");
            }

            if (candidates is not null)
            {
                var ordering = Versioning.Validate(candidates);
                if (!ordering.Accepted)
                {
                    errors.Add($"watch[{index}].candidates contains unparseable or duplicate versions: {string.Join(", ", ordering.RejectedCandidates)}.");
                }
            }

            if (kind is not null && candidates is not null)
            {
                watches.Add(new WatchConfiguration(id, kind.Value, package, candidates, feed));
            }
        }

        return watches.Count == array.GetArrayLength() ? watches : null;
    }

    private static ValidationConfiguration? ParseValidation(JsonElement root, List<string> errors)
    {
        if (!TryGetRequiredObject(root, "validation", "root", errors, out var element))
        {
            return null;
        }

        RejectUnknown(element, ValidationProperties, "validation", errors);
        var command = ReadRequiredString(element, "command", "validation", errors);
        var workingDirectory = ReadOptionalString(element, "workingDirectory", "validation", errors) ?? ".";
        var timeoutSeconds = ReadRequiredInt(element, "timeoutSeconds", "validation", errors);
        if (command is not null && (command.Contains('\0') || command.Length > 2000))
        {
            errors.Add("validation.command must be 1-2000 characters and cannot contain NUL.");
        }

        if (string.IsNullOrWhiteSpace(workingDirectory) || Path.IsPathRooted(workingDirectory) || workingDirectory.Contains('\0'))
        {
            errors.Add("validation.workingDirectory must be a non-empty repository-relative path.");
        }

        if (timeoutSeconds is < 1 or > 86400)
        {
            errors.Add("validation.timeoutSeconds must be between 1 and 86400.");
        }

        return command is null || timeoutSeconds is null ? null : new ValidationConfiguration(command, workingDirectory, timeoutSeconds.Value);
    }

    private static PolicyConfiguration? ParsePolicy(JsonElement root, List<string> errors)
    {
        if (!TryGetRequiredObject(root, "policy", "root", errors, out var element))
        {
            return null;
        }

        RejectUnknown(element, PolicyProperties, "policy", errors);
        var runs = ReadRequiredInt(element, "confirmationRuns", "policy", errors);
        if (runs is < 1 or > 5)
        {
            errors.Add("policy.confirmationRuns must be between 1 and 5.");
        }

        return runs is null ? null : new PolicyConfiguration(runs.Value);
    }

    private static WatchKind? ParseWatchKind(string? value, int index, List<string> errors)
    {
        if (value is "nuget-prerelease")
        {
            return WatchKind.NuGetPrerelease;
        }

        if (value is "sdk-preview")
        {
            return WatchKind.SdkPreview;
        }

        if (value is "runtime-preview")
        {
            return WatchKind.RuntimePreview;
        }

        if (value is not null)
        {
            errors.Add($"watch[{index}].kind must be 'nuget-prerelease', 'sdk-preview', or 'runtime-preview'.");
        }

        return null;
    }

    private static List<string>? ReadCandidateArray(JsonElement element, int index, List<string> errors)
    {
        if (!element.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
        {
            errors.Add($"watch[{index}].candidates must be a non-empty array of version strings.");
            return null;
        }

        var result = new List<string>();
        foreach (var candidate in candidates.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(candidate.GetString()))
            {
                errors.Add($"watch[{index}].candidates must contain only non-empty strings.");
                continue;
            }

            result.Add(candidate.GetString()!);
        }

        return result.Count == candidates.GetArrayLength() ? result : null;
    }

    /// <summary>
    /// Accepted feed URLs are retained verbatim in witnesses, JSON reports, console output, and
    /// Action summaries, and any query value, fragment, or user-info component can carry a
    /// credential under a name this tool cannot recognize. Configuration v1 therefore accepts only
    /// the form that cannot carry one: an absolute HTTP(S) URL with no user-info, query string, or
    /// fragment. A rejected feed is reported by index only, so the rejected value is never echoed.
    /// Credential-bearing feeds belong in environment-based authentication or a NuGet credential
    /// provider.
    /// </summary>
    private static bool IsSafeFeed(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var isHttpScheme = uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        return isHttpScheme
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
    }

    private static void RejectUnknown(JsonElement element, HashSet<string> allowed, string context, List<string> errors)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                errors.Add($"{context} contains unsupported property '{property.Name}'.");
            }
        }
    }

    private static string? ReadRequiredString(JsonElement parent, string name, string context, List<string> errors)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            errors.Add($"{context}.{name} is required and must be a non-empty string.");
            return null;
        }

        return value.GetString();
    }

    private static string? ReadOptionalString(JsonElement parent, string name, string context, List<string> errors)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{context}.{name} must be a string when specified.");
            return null;
        }

        return value.GetString();
    }

    private static int? ReadRequiredInt(JsonElement parent, string name, string context, List<string> errors)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            errors.Add($"{context}.{name} is required and must be an integer.");
            return null;
        }

        return result;
    }

    private static bool TryGetRequiredObject(JsonElement parent, string name, string context, List<string> errors, out JsonElement value)
    {
        if (!parent.TryGetProperty(name, out value) || value.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{context}.{name} is required and must be an object.");
            return false;
        }

        return true;
    }

    private static bool TryGetRequiredArray(JsonElement parent, string name, string context, List<string> errors, out JsonElement value)
    {
        if (!parent.TryGetProperty(name, out value) || value.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{context}.{name} is required and must be an array.");
            return false;
        }

        return true;
    }
}

internal static class PathUtilities
{
    public static string ResolveUnderRoot(string root, string path)
    {
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
    }

    public static bool IsWithinRoot(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = GetPathComparison(normalizedRoot);
        if (!string.Equals(normalizedPath, normalizedRoot, comparison)
            && !normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison)
            && !normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, comparison))
        {
            return false;
        }

        return !ContainsReparsePointBetween(normalizedRoot, normalizedPath, comparison);
    }

    public static string ToRepositoryRelative(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative == "." ? "." : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static bool ContainsReparsePointBetween(string root, string path, StringComparison comparison)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    {
                        return true;
                    }
                }
                catch (IOException)
                {
                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    return true;
                }
            }

            if (string.Equals(current, root, comparison))
            {
                return false;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, comparison))
            {
                return true;
            }

            current = parent;
        }

        return true;
    }

    private static StringComparison GetPathComparison(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            return StringComparison.OrdinalIgnoreCase;
        }

        // macOS can use either a case-sensitive or case-insensitive volume. Probe
        // the existing root's parent without creating or modifying any filesystem
        // entry, and use ordinal comparison whenever both spellings are distinct.
        var parent = Directory.GetParent(root);
        var leaf = Path.GetFileName(root);
        if (parent is not null && !string.IsNullOrEmpty(leaf))
        {
            var alternateLeaf = ToggleCase(leaf);
            if (!string.Equals(leaf, alternateLeaf, StringComparison.Ordinal))
            {
                try
                {
                    var matches = Directory.EnumerateFileSystemEntries(parent.FullName)
                        .Where(entry => string.Equals(Path.GetFileName(entry), leaf, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    var alternatePath = Path.Combine(parent.FullName, alternateLeaf);
                    var alternateExists = Directory.Exists(alternatePath) || File.Exists(alternatePath);
                    if (alternateExists && matches.Length == 1)
                    {
                        return StringComparison.OrdinalIgnoreCase;
                    }

                    if (matches.Length >= 2)
                    {
                        return StringComparison.Ordinal;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        return StringComparison.Ordinal;
    }

    private static string ToggleCase(string value)
    {
        var characters = value.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (!char.IsLetter(characters[index])) continue;
            characters[index] = char.IsUpper(characters[index])
                ? char.ToLowerInvariant(characters[index])
                : char.ToUpperInvariant(characters[index]);
            break;
        }

        return new string(characters);
    }
}
