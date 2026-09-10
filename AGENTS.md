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
pwsh -NoProfile -File scripts/security-audit.ps1
dotnet pack src/KeelMatrix.CompatRadar/KeelMatrix.CompatRadar.csproj -c Release --no-build --include-symbols --p:SymbolPackageFormat=snupkg --output artifacts/packages
pwsh -NoProfile -File scripts/inspect-package.ps1 -PackageDirectory artifacts/packages -ExpectedVersion 0.1.0
pwsh -NoProfile -File smoke/package-consumer-smoke.ps1 -PackagePath artifacts/packages/KeelMatrix.CompatRadar.0.1.0.nupkg
pwsh -NoProfile -File scripts/technical-validation-gate.ps1 -PreviewCandidate '11.0.100-preview.7.26381.103' -RealRepositoryPath $PWD
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
- `.github/workflows/ci.yml` validates the Windows, Linux, and macOS matrix; `release.yml` is tag-gated and inert until an approved release action.

## Validation strategy

Start with the matching test class, then run the test project, Release build, format verification, package inspection, and isolated package-consumer smoke. Record cross-platform or network checks that cannot run locally rather than inferring their results.
