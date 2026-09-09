using System.Text.RegularExpressions;

namespace KeelMatrix.CompatRadar;

internal static class Versioning
{
    private static readonly Regex VersionPattern = new(
        "^(?<core>\\d+(?:\\.\\d+){0,3})(?:-(?<pre>[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static CandidateValidationResult Validate(IReadOnlyList<string> candidates)
    {
        var parsed = new List<(string Value, CandidateVersion Version, string Key)>();
        var rejected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!TryParse(candidate, out var version))
            {
                rejected.Add(candidate);
                continue;
            }

            var key = CreateEquivalenceKey(version);
            if (parsed.Any(existing => existing.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                rejected.Add(candidate);
                continue;
            }

            parsed.Add((candidate, version, key));
        }

        var ordered = parsed
            .OrderBy(item => item.Version, CandidateVersionComparer.Instance)
            .ThenBy(item => item.Value, StringComparer.Ordinal)
            .Select(item => item.Value)
            .ToArray();
        return new CandidateValidationResult(rejected.Count == 0, ordered, rejected.OrderBy(x => x, StringComparer.Ordinal).ToArray(), rejected.Count == 0 ? null : "Unparseable or ambiguous candidate versions were rejected before execution.");
    }

    public static bool TryParse(string value, out CandidateVersion version)
    {
        version = new CandidateVersion([], []);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = VersionPattern.Match(value);
        if (!match.Success)
        {
            return false;
        }

        var core = new List<int>();
        foreach (var component in match.Groups["core"].Value.Split('.'))
        {
            if ((component.Length > 1 && component.StartsWith('0')) || !int.TryParse(component, out var number))
            {
                return false;
            }

            core.Add(number);
        }

        var identifiers = match.Groups["pre"].Success
            ? match.Groups["pre"].Value.Split('.')
            : Array.Empty<string>();
        var prerelease = new List<CandidateIdentifier>();
        foreach (var identifier in identifiers)
        {
            if (identifier.Length == 0)
            {
                return false;
            }

            var numeric = int.TryParse(identifier, out var numericValue);
            if (numeric && identifier.Length > 1 && identifier.StartsWith('0'))
            {
                return false;
            }

            prerelease.Add(new CandidateIdentifier(identifier, numeric, numericValue));
        }

        version = new CandidateVersion(core, prerelease);
        return true;
    }

    private static string CreateEquivalenceKey(CandidateVersion version)
    {
        var core = string.Join('.', version.Core.Concat(Enumerable.Repeat(0, 4 - version.Core.Count)));
        var pre = version.Prerelease.Count == 0
            ? string.Empty
            : "-" + string.Join('.', version.Prerelease.Select(x => x.Value.ToLowerInvariant()));
        return core + pre;
    }
}

internal sealed class CandidateVersionComparer : IComparer<CandidateVersion>
{
    public static CandidateVersionComparer Instance { get; } = new();

    public int Compare(CandidateVersion? left, CandidateVersion? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;

        var coreLength = Math.Max(left.Core.Count, right.Core.Count);
        for (var index = 0; index < coreLength; index++)
        {
            var leftPart = index < left.Core.Count ? left.Core[index] : 0;
            var rightPart = index < right.Core.Count ? right.Core[index] : 0;
            var coreComparison = leftPart.CompareTo(rightPart);
            if (coreComparison != 0)
            {
                return coreComparison;
            }
        }

        if (left.Prerelease.Count == 0 && right.Prerelease.Count == 0) return 0;
        if (left.Prerelease.Count == 0) return 1;
        if (right.Prerelease.Count == 0) return -1;

        var length = Math.Max(left.Prerelease.Count, right.Prerelease.Count);
        for (var index = 0; index < length; index++)
        {
            if (index >= left.Prerelease.Count) return -1;
            if (index >= right.Prerelease.Count) return 1;
            var leftPart = left.Prerelease[index];
            var rightPart = right.Prerelease[index];
            if (leftPart.IsNumeric && rightPart.IsNumeric)
            {
                var numericComparison = leftPart.NumericValue.CompareTo(rightPart.NumericValue);
                if (numericComparison != 0) return numericComparison;
            }
            else if (leftPart.IsNumeric != rightPart.IsNumeric)
            {
                return leftPart.IsNumeric ? -1 : 1;
            }
            else
            {
                var lexicalComparison = string.Compare(leftPart.Value, rightPart.Value, StringComparison.Ordinal);
                if (lexicalComparison != 0) return lexicalComparison;
            }
        }

        return 0;
    }
}
