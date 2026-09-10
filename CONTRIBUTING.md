# Contributing

Thank you for helping improve CompatRadar.

## Development

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
pwsh -NoProfile -File scripts/technical-validation-gate.ps1 -PreviewCandidate '11.0.100-preview.7.26381.103' -RealRepositoryPath $PWD
```

The release workflow uses the same SDK and controlled restore. It is tag-gated and must not be run as part of ordinary development.

Keep changes focused, add regression coverage for behavior changes, and update the README when the command or report contract changes. Do not add credentials, repository contents, local telemetry files, or generated build output.

## Pull requests

Describe the user-facing behavior, tests run, package/consumer verification, and any platform or network evidence that was unavailable. Security fixes should follow [SECURITY.md](SECURITY.md). Privacy and durable contract changes should also update [PRIVACY.md](PRIVACY.md) and [docs/compatibility.md](docs/compatibility.md).
