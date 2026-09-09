using System.Diagnostics;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KeelMatrix.CompatRadar;

internal sealed record ParsedCommand(string FileName, IReadOnlyList<string> Arguments)
{
    public string Display => string.Join(" ", new[] { FileName }.Concat(Arguments.Select(Quote)));

    private static string Quote(string value) => value.Length == 0 || value.Any(char.IsWhiteSpace) || value.Contains('"')
        ? $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\""
        : value;
}

internal sealed record ProcessExecutionResult(
    int ExitCode,
    bool TimedOut,
    bool Cancelled,
    bool TerminationRequested,
    string? FailureKind,
    string Command,
    string WorkingDirectory,
    string Stdout,
    string Stderr,
    string Summary,
    string NormalizedSignature,
    string Fingerprint);

internal static class CommandParser
{
    public static bool TryParse(string command, out ParsedCommand? parsed, out string? error)
    {
        parsed = null;
        error = null;
        if (string.IsNullOrWhiteSpace(command) || command.Contains('\0'))
        {
            error = "The validation command must be non-empty and cannot contain NUL.";
            return false;
        }

        var tokens = new List<string>();
        var token = new StringBuilder();
        var inSingle = false;
        var inDouble = false;
        var escaping = false;
        foreach (var character in command.Trim())
        {
            if (escaping)
            {
                token.Append(character);
                escaping = false;
                continue;
            }

            if (character == '\\' && inDouble)
            {
                escaping = true;
                continue;
            }

            if (character == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                continue;
            }

            if (character == '"' && !inSingle)
            {
                inDouble = !inDouble;
                continue;
            }

            if (char.IsWhiteSpace(character) && !inSingle && !inDouble)
            {
                if (token.Length > 0)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                }
            }
            else
            {
                token.Append(character);
            }
        }

        if (escaping) token.Append('\\');
        if (inSingle || inDouble)
        {
            error = "The validation command contains an unmatched quote.";
            return false;
        }

        if (token.Length > 0) tokens.Add(token.ToString());
        if (tokens.Count == 0)
        {
            error = "The validation command must contain an executable.";
            return false;
        }

        parsed = new ParsedCommand(tokens[0], tokens.Skip(1).ToArray());
        return true;
    }
}

internal static class ProcessRunner
{
    private static readonly Regex SecretAssignmentPattern = new(
        """(?ix)(?<prefix>(?:"[^"]*(?:key|token|secret|password|credential|connection[-_ ]?string|passphrase|passwd|pwd|auth|authorization|signature)[^"]*"|'[^']*(?:key|token|secret|password|credential|connection[-_ ]?string|passphrase|passwd|pwd|auth|authorization|signature)[^']*'|(?<![A-Za-z0-9])[A-Za-z0-9_.-]*(?:key|token|secret|password|credential|connection[-_ ]?string|passphrase|passwd|pwd|auth|authorization|signature)[A-Za-z0-9_.-]*)\s*[:=]\s*)(?:Bearer\s+)?(?:"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|[^,\s}\]]+)""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex KeyValuePattern = new(
        """(?<prefix>(?:"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|[A-Za-z_][A-Za-z0-9_.-]*)\s*[:=]\s*)(?<value>"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|[^,\s}\]]+)""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BearerPattern = new(@"\bBearer\s+\S+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AbsolutePathPattern = new("(?i)(?:[A-Z]:[\\\\/]|/)[^\\r\\n ]+", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new("\\b\\d+(?:\\.\\d+){1,3}(?:-[0-9A-Za-z.-]+)?\\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static async Task<ProcessExecutionResult> RunAsync(
        ParsedCommand command,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command.FileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in command.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment)
        {
            process.StartInfo.Environment[key] = value;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            process.Start();
            var stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, RadarContract.OutputLimitBytes);
            var stderrTask = ReadBoundedAsync(process.StandardError.BaseStream, RadarContract.OutputLimitBytes);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            var timedOut = false;
            var cancelled = false;
            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = timeoutCts.IsCancellationRequested;
                cancelled = !timedOut && cancellationToken.IsCancellationRequested;
                TryKillTree(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var normalized = NormalizeDiagnostic($"{stdout}\n{stderr}", workingDirectory);
            var signature = normalized.Length == 0
                ? timedOut ? "process timed out" : cancelled ? "process cancelled" : "no diagnostic"
                : normalized;
            var exitCode = timedOut || cancelled ? -1 : process.ExitCode;
            var summary = Sanitize(FindUsefulLine($"{stderr}\n{stdout}") ?? signature, workingDirectory);
            return new ProcessExecutionResult(
                exitCode,
                timedOut,
                cancelled,
                timedOut || cancelled,
                timedOut ? "timeout" : cancelled ? "cancellation" : exitCode == 0 ? null : "exit-code",
                command.Display,
                workingDirectory,
                Redact(stdout),
                Redact(stderr),
                summary,
                signature,
                Fingerprint(signature));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or DirectoryNotFoundException or IOException)
        {
            var signature = $"process launch failed: {exception.GetType().Name}";
            return new ProcessExecutionResult(
                -1,
                false,
                false,
                false,
                "process-launch-failed",
                command.Display,
                workingDirectory,
                string.Empty,
                string.Empty,
                signature,
                signature,
                Fingerprint(signature));
        }
    }

    public static string NormalizeDiagnostic(string text, string workingDirectory)
    {
        var normalized = Redact(text).Replace(workingDirectory, "<repo>", StringComparison.OrdinalIgnoreCase);
        normalized = AbsolutePathPattern.Replace(normalized, "<path>");
        normalized = Regex.Replace(normalized, "(?i)(line\\s+|:)\\d+", "$1<line>", RegexOptions.CultureInvariant);
        normalized = VersionPattern.Replace(normalized, "<version>");
        normalized = Regex.Replace(normalized, "\\s+", " ", RegexOptions.CultureInvariant).Trim();
        return normalized.Length > 2000 ? normalized[..2000] : normalized;
    }

    public static string Redact(string text)
    {
        var redacted = SecretAssignmentPattern.Replace(text, match => match.Groups["prefix"].Value + "<redacted>");
        redacted = KeyValuePattern.Replace(redacted, match =>
        {
            var value = match.Groups["value"].Value;
            return LooksLikeOpaqueCredential(value)
                ? match.Groups["prefix"].Value + "<redacted>"
                : match.Value;
        });
        return BearerPattern.Replace(redacted, "Bearer <redacted>");
    }

    private static bool LooksLikeOpaqueCredential(string value)
    {
        var unquoted = value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;
        if (unquoted.Length < 16 || unquoted.Any(char.IsWhiteSpace) || unquoted.Equals("<redacted>", StringComparison.Ordinal))
        {
            return false;
        }

        var hasLower = unquoted.Any(char.IsLower);
        var hasUpper = unquoted.Any(char.IsUpper);
        var hasDigit = unquoted.Any(char.IsDigit);
        var hasSymbol = unquoted.Any(character => !char.IsLetterOrDigit(character));
        var categories = (hasLower ? 1 : 0) + (hasUpper ? 1 : 0) + (hasDigit ? 1 : 0) + (hasSymbol ? 1 : 0);
        return categories >= 3 || (unquoted.Length >= 24 && unquoted.Count(character => character is '-' or '_' or '.') >= 2);
    }

    private static string Sanitize(string text, string workingDirectory)
    {
        var normalized = NormalizeDiagnostic(text, workingDirectory);
        var line = FindUsefulLine(normalized) ?? "no diagnostic";
        return line.Length > 500 ? line[..500] : line;
    }

    private static string? FindUsefulLine(string text)
    {
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => new
            {
                Line = line,
                Score = (line.Contains("incompatible", StringComparison.OrdinalIgnoreCase) ? 50 : 0)
                    + (line.Contains("error", StringComparison.OrdinalIgnoreCase) ? 20 : 0)
                    + (line.Contains("failed", StringComparison.OrdinalIgnoreCase) ? 10 : 0)
                    + (line.Contains("exception", StringComparison.OrdinalIgnoreCase) ? 5 : 0)
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Line.Length)
            .Select(item => item.Line)
            .FirstOrDefault();
    }

    private static string Fingerprint(string normalized)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, int limit)
    {
        using var reader = new StreamReader(stream);
        var buffer = new char[4096];
        var builder = new StringBuilder(Math.Min(limit, 4096));
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false)) > 0)
        {
            var remaining = limit - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(remaining, read));
            }

            if (read > Math.Max(remaining, 0)) truncated = true;
        }

        if (truncated) builder.Append("\n[output truncated]");
        return builder.ToString();
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
