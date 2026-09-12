using NuGet.Versioning;

namespace KeelMatrix.CompatRadar;

/// <summary>
/// Candidate version handling delegates to NuGet's own version semantics.
///
/// Candidates are user input, so the original string is preserved for console output, reports,
/// witnesses, and reproduction commands. Validation, duplicate detection, ordering, and first-bad
/// boundaries instead use <see cref="VersionComparer.VersionRelease"/> so the tool agrees with NuGet:
/// SemVer 2 build metadata is accepted and never changes precedence, prerelease labels compare
/// case-insensitively, and NuGet-normalized equivalents such as <c>1.0</c> and <c>1.0.0</c> describe
/// the same candidate.
/// </summary>
internal static class Versioning
{
    /// <summary>
    /// Precedence is metadata-insensitive, so duplicate detection is too: two candidates that NuGet
    /// orders as the same version cannot both participate in a first-bad boundary.
    /// </summary>
    private static readonly NuGetVersionPrecedence Precedence = NuGetVersionPrecedence.Instance;

    public static CandidateValidationResult Validate(IReadOnlyList<string> candidates)
    {
        var parsed = new List<(string Value, NuGetVersion Version)>();
        var rejected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!TryParse(candidate, out var version))
            {
                rejected.Add(candidate);
                continue;
            }

            if (parsed.Any(existing => Precedence.Compare(existing.Version, version) == 0))
            {
                rejected.Add(candidate);
                continue;
            }

            parsed.Add((candidate, version));
        }

        var ordered = parsed
            .OrderBy(item => item.Version, Precedence)
            .ThenBy(item => item.Value, StringComparer.Ordinal)
            .Select(item => item.Value)
            .ToArray();
        return new CandidateValidationResult(
            rejected.Count == 0,
            ordered,
            rejected.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            rejected.Count == 0 ? null : "Unparseable or duplicate candidate versions were rejected before execution.");
    }

    public static bool TryParse(string value, out NuGetVersion version)
    {
        if (string.IsNullOrWhiteSpace(value) || !NuGetVersion.TryParse(value, out var parsed))
        {
            version = null!;
            return false;
        }

        version = parsed;
        return true;
    }

    private sealed class NuGetVersionPrecedence : IComparer<NuGetVersion>
    {
        public static NuGetVersionPrecedence Instance { get; } = new();

        public int Compare(NuGetVersion? left, NuGetVersion? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            return VersionComparer.VersionRelease.Compare(left, right);
        }
    }
}
