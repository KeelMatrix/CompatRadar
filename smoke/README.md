# Package-consumer smoke

This directory contains the package-consumer smoke for `KeelMatrix.CompatRadar`. It validates the
built `.nupkg` in an isolated temporary environment; it is a maintainer check, not the normal
consumer installation path. Use the root [README](../README.md) for package installation and use.

## Run the smoke

Run the commands from the repository root. The prerequisites are the pinned .NET SDK selected by
`global.json`, PowerShell 7 (`pwsh`), and a successful Release build:

```powershell
dotnet build KeelMatrix.CompatRadar.sln -c Release
dotnet pack src/KeelMatrix.CompatRadar/KeelMatrix.CompatRadar.csproj -c Release --no-build
pwsh -NoProfile -File smoke/package-consumer-smoke.ps1 -PackagePath artifacts/packages/KeelMatrix.CompatRadar.0.1.0.nupkg
```

## Expected result

Expected output ends with `Package consumer smoke passed.` and exit code `0`.

The smoke creates isolated `NUGET_PACKAGES`, HTTP/plugin caches, tool path, and temporary `NuGet.Config`. Package-source mapping pins `KeelMatrix.CompatRadar` to the supplied local artifact while normal dependencies use nuget.org, so an old cached or published CompatRadar package cannot satisfy the install. Its fixture repository references a real local dependency package: the compatible version keeps the API the fixture calls, a later version changes behavior, and the newest version removes that API, so the observed regressions come from the dependency state rather than from anything the tool injects. It proves package installation, stable-identical compatibility, a behavior-change regression, a compile-time incompatibility, baseline inconclusive behavior, flaky inconclusive behavior, diagnostic redaction, and fail-closed feed validation from the installed tool, including credentials hidden in user-info, under an unrecognized query parameter name, and in a URI fragment. It does not use `dotnet run` against the source project.

## Cleanup and side effects

The script removes its temporary consumer directory and restores the telemetry-related environment variables when it exits. The built package remains in `artifacts/packages`; no source, test, or consumer repository files are modified.
