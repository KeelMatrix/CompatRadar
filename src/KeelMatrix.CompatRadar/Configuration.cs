using System.Text.Json;

using System.Text.RegularExpressions;

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
            var id = ReadOptionalString(element, "id") ?? $"watch-{index:000}";
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || !ids.Add(id))
            {
                errors.Add($"watch[{index}].id must be unique and contain 1-64 characters.");
            }

            var kindText = ReadRequiredString(element, "kind", $"watch[{index}]", errors);
            var kind = ParseWatchKind(kindText, index, errors);
            var candidates = ReadCandidateArray(element, index, errors);
            var package = ReadOptionalString(element, "package");
            var feed = ReadOptionalString(element, "feed");

            if (feed is not null && !IsSafeFeed(feed))
            {
                errors.Add($"watch[{index}].feed must be an absolute HTTP(S) URL without embedded credentials.");
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
                errors.Add($"watch[{index}] SDK previews cannot specify package or feed.");
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
        var workingDirectory = ReadOptionalString(element, "workingDirectory") ?? ".";
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

        if (value is not null)
        {
            errors.Add($"watch[{index}].kind must be 'nuget-prerelease' or 'sdk-preview'.");
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

    private static bool IsSafeFeed(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        return uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(parameter => parameter.Split('=', 2)[0])
            .Select(parameter => Uri.UnescapeDataString(parameter))
            .All(parameter => !IsCredentialQueryName(parameter));
    }

    private static bool IsCredentialQueryName(string value)
    {
        var camelSeparated = Regex.Replace(value, "([a-z0-9])([A-Z])", "$1_$2", RegexOptions.CultureInvariant);
        var normalized = new string(camelSeparated.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (normalized is "key" or "apikey" or "token" or "secret" or "password" or "credential" or "credentials" or "sig" or "signature" or "auth" or "authorization" or "connectionstring")
        {
            return true;
        }

        var parts = Regex.Split(camelSeparated, "[^A-Za-z0-9]+", RegexOptions.CultureInvariant)
            .Where(part => part.Length > 0)
            .Select(part => part.ToLowerInvariant())
            .ToArray();
        return parts.Any(part => part is "key" or "token" or "secret" or "password" or "credential" or "credentials" or "sig" or "signature" or "auth" or "authorization")
            || (parts.Contains("connection", StringComparer.Ordinal) && parts.Contains("string", StringComparer.Ordinal));
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

    private static string? ReadOptionalString(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
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
        var normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedPath, normalizedRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    public static string ToRepositoryRelative(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative == "." ? "." : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    }
}
