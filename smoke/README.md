# Package-consumer smoke

Build the Release package, then install and exercise that exact `.nupkg` from an isolated temporary environment:

```powershell
dotnet pack src/KeelMatrix.CompatRadar/KeelMatrix.CompatRadar.csproj -c Release --no-build
powershell -ExecutionPolicy Bypass -File smoke/package-consumer-smoke.ps1 artifacts/packages/KeelMatrix.CompatRadar.0.1.0.nupkg
```

The smoke proves package installation, stable-identical compatibility, a planted future regression, baseline inconclusive behavior, flaky inconclusive behavior, diagnostic redaction, and fail-closed credential-bearing feed validation from the installed tool. It does not use `dotnet run` against the source project.
