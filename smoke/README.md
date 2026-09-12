# Package-consumer smoke

Build the Release package, then install and exercise that exact `.nupkg` from an isolated temporary environment:

```powershell
dotnet pack src/KeelMatrix.CompatRadar/KeelMatrix.CompatRadar.csproj -c Release --no-build
pwsh -NoProfile -File smoke/package-consumer-smoke.ps1 -PackagePath artifacts/packages/KeelMatrix.CompatRadar.0.1.0.nupkg
```

The smoke creates isolated `NUGET_PACKAGES`, HTTP/plugin caches, tool path, and temporary `NuGet.Config`. Package-source mapping pins `KeelMatrix.CompatRadar` to the supplied local artifact while normal dependencies use nuget.org, so an old cached or published CompatRadar package cannot satisfy the install. Its fixture repository references a real local dependency package: the compatible version keeps the API the fixture calls, a later version changes behavior, and the newest version removes that API, so the observed regressions come from the dependency state rather than from anything the tool injects. It proves package installation, stable-identical compatibility, a behavior-change regression, a compile-time incompatibility, baseline inconclusive behavior, flaky inconclusive behavior, diagnostic redaction, and fail-closed credential-bearing feed validation from the installed tool. It does not use `dotnet run` against the source project.
