# KeelMatrix.CompatRadar

Dependency bots tell you a new version exists. CompatRadar tells you whether a future version actually breaks your repository while today's stable state still passes—and gives you a reproducible failure before the upgrade becomes urgent.

CompatRadar is a local-first .NET tool. It compares an explicitly selected NuGet prerelease or SDK/runtime preview with the current stable control, repeats candidate failures, reports uncertainty honestly, and localizes a first bad candidate only when the evidence is monotonic.

## Install

```bash
dotnet tool install --global KeelMatrix.CompatRadar --version 0.1.0
```

Update or remove it with:

```bash
dotnet tool update --global KeelMatrix.CompatRadar --version 0.1.0
dotnet tool uninstall --global KeelMatrix.CompatRadar
```

## Five-minute NuGet prerelease check

Create `compat-radar.json` in the repository root:

```json
{
  "version": 1,
  "control": { "sdk": "current" },
  "watch": [
    {
      "kind": "nuget-prerelease",
      "package": "Example.Dependency",
      "candidates": ["9.0.0-rc.1", "9.0.0-rc.2"],
      "feed": "https://api.nuget.org/v3/index.json"
    }
  ],
  "validation": {
    "command": "dotnet test -c Release",
    "workingDirectory": ".",
    "timeoutSeconds": 900
  },
  "policy": { "confirmationRuns": 2 }
}
```

Run it:

```bash
compat-radar config validate
compat-radar check --report compat-radar-report.json
```

The command is explicit: CompatRadar does not infer every dependency worth watching. The watched package must already be referenced by the repository in a `PackageReference` or central `PackageVersion` declaration. The candidate override is applied only to an isolated temporary copy.

## SDK/runtime preview check

Use the same schema with an SDK/runtime channel:

```json
{
  "version": 1,
  "control": { "sdk": "current" },
  "watch": [
    {
      "kind": "sdk/runtime-preview",
      "candidates": ["9.0.120", "10.0.400"],
      "runtime": "sdk"
    }
  ],
  "validation": {
    "command": "dotnet test -c Release",
    "workingDirectory": ".",
    "timeoutSeconds": 900
  },
  "policy": { "confirmationRuns": 2 }
}
```

For this channel, each candidate is applied to an isolated `global.json`. The requested SDK must be installed or otherwise available to the local .NET host. A runtime preview is selected through the SDK/runtime installation used by that `global.json`; the tool does not download SDKs for you.

## Configuration

Schema version `1` has these fields:

- `control.sdk`: must be `current`; this is the stable control in the current execution environment.
- `watch`: one or more explicit channels. Each channel has `kind`, `candidates`, and an optional `id`. NuGet channels also require `package` and may specify an HTTP(S) `feed` without embedded credentials. SDK/runtime channels may specify `runtime` as `sdk` or `runtime`.
- `validation.command`: an executable plus arguments. Commands are started directly; shell syntax is not evaluated by CompatRadar.
- `validation.workingDirectory`: a repository-relative directory.
- `validation.timeoutSeconds`: bounded command timeout from 1 through 86400 seconds.
- `policy.confirmationRuns`: 1 through 5 attempts for the stable control and each candidate.

Candidate versions must be parseable NuGet-style versions. Duplicate or ambiguous versions fail configuration validation before any command is run. Candidates are ordered deterministically by core version and prerelease precedence.

## Stable control and result states

The stable control always runs first in each comparison window. A candidate cannot become a future regression if stable does not pass. This prevents an existing repository failure from being blamed on a future dependency.

The report uses these stable states:

| State | Meaning |
|---|---|
| `COMPATIBLE` | Stable and candidate both passed the configured confirmation policy. |
| `FUTURE_REGRESSION` | Stable passed and every candidate confirmation failed. |
| `INCONCLUSIVE_BASELINE_FAILED` | Stable failed or was flaky, so the candidate was not treated as evidence. |
| `INCONCLUSIVE_FLAKY` | Candidate confirmation attempts disagreed. |
| `INCONCLUSIVE_EXECUTION` | Restore, launch, timeout, cancellation, or candidate materialization could not complete trustworthily. |
| `UNSUPPORTED` | A future channel is outside the explicitly supported v1 adapter contract. |

Exit codes are:

- `0`: trustworthy analysis completed with no confirmed blocking future regression;
- `1`: at least one confirmed future regression;
- `2`: analysis could not complete trustworthily.

Flaky and inconclusive results never become a silent clean-compatibility result.

## First-bad and non-monotonic behavior

When ordered candidates are `PASS, PASS, FAIL, FAIL`, CompatRadar reports the first confirmed failing candidate and its witness. When candidates are `PASS, FAIL, PASS`, it reports the observed failing candidate set and makes no permanent first-bad claim. A later passing preview is evidence that the sequence is non-monotonic.

Each future-regression finding includes the repository revision, candidate identity, validation command, stable/candidate evidence, normalized failure signature, fingerprint, and a reproduction hint:

```bash
compat-radar reproduce package-watch-9.0.0-rc.2 --report compat-radar-report.json
```

`reproduce` reads a prior local report and emits the recorded witness/configuration. It does not rerun the repository command.

## JSON reports and artifacts

Use `--format json` for CI and `--report <path>` to write an artifact explicitly. Reports are schema-versioned and deterministic for equivalent inputs. They contain sanitized bounded diagnostics, repository-relative configuration paths, and no raw stdout/stderr. Keep reports as short-lived CI artifacts when they may contain project-specific failure summaries; CompatRadar has no hosted report service and does not upload them.

## GitHub Action

The repository includes a composite Action wrapper. Install the tool in the job, then call the local Action:

```yaml
- name: Install CompatRadar
  shell: bash
  run: dotnet tool install --global KeelMatrix.CompatRadar --version 0.1.0

- name: Check future compatibility
  uses: ./path/to/CompatRadar
  with:
    config: compat-radar.json
    report: artifacts/compat-radar-report.json
```

The wrapper invokes the same `compat-radar check` command, appends the report to `GITHUB_STEP_SUMMARY`, and emits a workflow error annotation for exit code `1`. No hosted dashboard is required. This repository intentionally contains no CI workflow files.

## Security and privacy

CompatRadar executes the configured repository restore/build/test command and is not a sandbox. Run it only in a trusted local or CI environment. Comparisons use safe temporary directories, do not follow reparse points, do not mutate the active worktree, bound process time and captured output, terminate process trees after timeout/cancellation, reject malformed candidate definitions, and sanitize diagnostics. Optional feeds must be supplied without embedded credentials; credentials are never printed.

Telemetry uses `KeelMatrix.Telemetry` only after the first trustworthy stable-versus-future comparison. It is best-effort, pseudonymous, and at most weekly. It never receives package choices, versions, repository names, paths, commands, test names, logs, source, environment variables, secrets, URLs, or reports. Set `KEELMATRIX_NO_TELEMETRY=1` to opt out. Local KeelMatrix development and CI should set that variable.

## Support and limitations

The tool targets `net8.0` and supports Windows, Linux, and macOS operations in the product design. The current local evidence covers Windows only; Linux/macOS execution and remote CI have not been run for this candidate. The v1 adapters are explicit NuGet prerelease package overrides and SDK/runtime preview selection through `global.json`.

CompatRadar does not manage dependencies, open update pull requests, discover all dependencies, run hosted builds, provide accounts or scheduling, send notifications, support non-.NET ecosystems, generate patches, or guarantee that every future incompatibility will be predicted.

## How this differs from dependency bots

Dependabot and Renovate answer “what versions are available?” and can automate update pull requests. CompatRadar answers “does this explicitly selected future .NET state break this repository while today's stable control passes?” It confirms the failure, distinguishes baseline failure and flakiness, avoids false monotonic claims, and leaves a local reproduction witness for the maintainer.
