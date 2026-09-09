# CompatRadar development guide

## Navigation

- `src/KeelMatrix.CompatRadar` contains the `compat-radar` .NET tool, configuration loader, isolated execution engine, report contract, and telemetry adapter.
- `tests/KeelMatrix.CompatRadar.Tests` contains durable behavior, process, configuration, report, CLI, and non-mutation coverage.
- `action.yml` and `scripts/` contain the GitHub Action wrapper.
- `smoke/` contains the isolated package-consumer smoke script.
- `artifacts/` is disposable local build and package output and is ignored by Git.

## Commands

```text
dotnet restore KeelMatrix.CompatRadar.sln
dotnet build KeelMatrix.CompatRadar.sln -c Release --no-restore
dotnet test KeelMatrix.CompatRadar.sln -c Release --no-build
dotnet format KeelMatrix.CompatRadar.sln --verify-no-changes
dotnet pack src/KeelMatrix.CompatRadar/KeelMatrix.CompatRadar.csproj -c Release --no-build
```

Run the tool from source:

```text
dotnet run --project src/KeelMatrix.CompatRadar -- config validate
dotnet run --project src/KeelMatrix.CompatRadar -- check --format json
```

## Invariants

- The package is one `net8.0` .NET tool with command `compat-radar`; it has no supported library API.
- `compat-radar.json` schema version `1` and report schema version `1` are compatibility contracts.
- Stable control always runs before a future regression can be reported.
- Candidate failures that are not reproducible are inconclusive, never silently compatible.
- Comparisons use isolated temporary copies. The caller's active worktree is never modified by analysis.
- Reparse points and symbolic links are not followed while materializing a repository.
- Reports use repository-relative paths and sanitized bounded diagnostics.
- Telemetry is best-effort and runs only after a trustworthy completed comparison; `KEELMATRIX_NO_TELEMETRY=1` suppresses it.
- No GitHub Actions workflow files are included in this phase. The checked-in Action is a local wrapper around an installed `compat-radar` command.

## Validation strategy

Start with the matching test class, then run the test project, Release build, format verification, package inspection, and isolated package-consumer smoke. Record cross-platform or network checks that cannot run locally rather than inferring their results.
