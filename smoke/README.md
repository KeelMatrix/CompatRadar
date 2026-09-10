# Package-consumer smoke

Build the Release package, then install and exercise that exact `.nupkg` from an isolated temporary environment:

```powershell
dotnet pack src/KeelMatrix.CompatRadar/KeelMatrix.CompatRadar.csproj -c Release --no-build
pwsh -NoProfile -File smoke/package-consumer-smoke.ps1 -PackagePath artifacts/packages/KeelMatrix.CompatRadar.0.1.0.nupkg
```

The smoke creates isolated `NUGET_PACKAGES`, HTTP/plugin caches, tool path, and temporary `NuGet.Config`. Package-source mapping pins `KeelMatrix.CompatRadar` to the supplied local artifact while normal dependencies use nuget.org, so an old cached or published CompatRadar package cannot satisfy the install. It proves package installation, stable-identical compatibility, a planted future regression, baseline inconclusive behavior, flaky inconclusive behavior, diagnostic redaction, and fail-closed credential-bearing feed validation from the installed tool. It does not use `dotnet run` against the source project.
