# Contributing

Thank you for helping improve CompatRadar.

## Repository layout

- `src/KeelMatrix.CompatRadar` contains the `compat-radar` .NET tool, configuration loader, isolated execution engine, report contract, and telemetry adapter.
- `tests/KeelMatrix.CompatRadar.Tests` contains durable behavior, process, configuration, report, CLI, and non-mutation coverage.
- `action.yml` and `scripts/` contain the GitHub Action wrapper and repository validation scripts.
- `smoke/` contains the isolated package-consumer smoke script.
- `artifacts/` is disposable local build and package output and is ignored by Git.

## Before you begin

Install the pinned .NET 8 SDK from `global.json`, then run:

```powershell
$env:KEELMATRIX_NO_TELEMETRY = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
dotnet restore KeelMatrix.CompatRadar.sln --configfile NuGet.config
dotnet build KeelMatrix.CompatRadar.sln -c Release --no-restore -warnaserror
dotnet test KeelMatrix.CompatRadar.sln -c Release --no-build --no-restore
dotnet format KeelMatrix.CompatRadar.sln --verify-no-changes
pwsh -NoProfile -File scripts/security-audit.ps1
dotnet pack src/KeelMatrix.CompatRadar/KeelMatrix.CompatRadar.csproj -c Release --no-build --no-restore --include-symbols --p:SymbolPackageFormat=snupkg --output artifacts/packages
pwsh -NoProfile -File scripts/inspect-package.ps1 -PackageDirectory artifacts/packages -ExpectedVersion 0.1.0
pwsh -NoProfile -File smoke/package-consumer-smoke.ps1 -PackagePath artifacts/packages/KeelMatrix.CompatRadar.0.1.0.nupkg
pwsh -NoProfile -File scripts/validation-corpus.ps1 -PreviewCandidate '11.0.100-preview.7.26381.103' -RealRepositoryPath $PWD
```

## Development

Run the tool from source when developing a focused change:

```powershell
dotnet run --project src/KeelMatrix.CompatRadar -- config validate
dotnet run --project src/KeelMatrix.CompatRadar -- check --format json
```

The [validation corpus guide](docs/validation-corpus.md) describes the optional preview and pinned-repository evidence in more detail.

The release workflow uses the same SDK and controlled restore. It is tag-gated and must not be run as part of ordinary development.

Keep changes focused, add regression coverage for behavior changes, and update the README when the command or report contract changes. Do not add credentials, repository contents, local telemetry files, or generated build output.

## Invariants

- The package is one `net8.0` .NET tool with command `compat-radar`; it has no supported library API.
- `compat-radar.json` schema version `1` and report schema version `1` are compatibility contracts.
- Stable control always runs before a future regression can be reported.
- Candidate failures that are not reproducible are inconclusive, never silently compatible.
- Comparisons use isolated temporary copies. The caller's active worktree is never modified by analysis.
- The comparison environment never tells the validation command which candidate is under test, and it clears inherited MSBuild SDK/tool-path pinning and node reuse so stable and candidate states cannot share build state or silently resolve a different SDK.
- Reparse points and symbolic links are not followed while materializing a repository.
- Reports use repository-relative paths and sanitized bounded diagnostics, and each witness records the deterministic content identity of the materialized state plus whether the working tree was dirty.
- Telemetry is best-effort and runs only after a trustworthy completed comparison; `KEELMATRIX_NO_TELEMETRY=1` suppresses it.
- `.github/workflows/ci.yml` validates the Windows, Linux, and macOS matrix; `release.yml` is tag-gated and inert until an approved release action.

## Validation strategy

Start with the matching test class, then run the test project, Release build, format verification, package inspection, and isolated package-consumer smoke. Record cross-platform or network checks that cannot run locally rather than inferring their results.

## Pull requests

Describe the user-facing behavior, tests run, package/consumer verification, and any platform or network evidence that was unavailable. Security fixes should follow [SECURITY.md](SECURITY.md). Privacy and durable contract changes should also update [PRIVACY.md](PRIVACY.md) and [docs/compatibility.md](docs/compatibility.md).

## Release preparation

Before creating a release tag, finalize the target entry in `CHANGELOG.md`, commit and push that change, and verify the exact commit with the repository changelog contract:

```powershell
$releaseVersion = '0.1.0'
$releaseCommit = (git rev-parse HEAD).Trim()
pwsh -NoProfile -File scripts/Test-ChangelogContract.ps1 -RepositoryPath $PWD -ChangelogPath CHANGELOG.md -ExpectedVersion $releaseVersion -ExpectedPackageVersion $releaseVersion -ExpectedCommit $releaseCommit
```

Run this check after the finalization commit and before creating the tag. The tag-triggered release workflow runs the same check again against the checked-out commit before restoring, packing, or publishing.
