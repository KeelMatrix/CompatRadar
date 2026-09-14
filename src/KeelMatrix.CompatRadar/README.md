# KeelMatrix.CompatRadar

CompatRadar is a .NET global or local tool for .NET maintainers who need to test an explicitly selected future SDK, runtime, or NuGet prerelease against a real repository. It runs the stable control first, confirms reproducible candidate failures, and leaves a local compatibility report and reproduction witness.

## Install

Install version `0.1.0` globally:

```bash
dotnet tool install --global KeelMatrix.CompatRadar --version 0.1.0
```

Update it with:

```bash
dotnet tool update --global KeelMatrix.CompatRadar --version 0.1.0
```

Remove it with:

```bash
dotnet tool uninstall --global KeelMatrix.CompatRadar
```

The command is `compat-radar`.

## Quick Start

From a repository that already references the package you want to watch, create `compat-radar.json` with explicit candidates:

```json
{
  "version": 1,
  "control": { "sdk": "current" },
  "watch": [
    {
      "kind": "nuget-prerelease",
      "package": "Example.Dependency",
      "candidates": ["9.0.0-rc.1", "9.0.0-rc.2"]
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

Replace the example package and candidate versions with the versions you intend to test, then run:

```bash
compat-radar check
```

Use `compat-radar config validate` before a comparison when you want to validate the configuration without running the repository command. Save a machine-readable report with `compat-radar check --format json --report compat-radar-report.json`.

## Important Limitations

- CompatRadar tests only explicitly configured candidates. It does not discover every dependency or act as a dependency update bot.
- The watched package must already be used by the repository. SDK and runtime preview candidates must already be installed; CompatRadar does not download them or silently fall back to another version.
- Execution is local-first. CompatRadar runs your configured restore/build/test command in an isolated temporary copy, is not a sandbox, and does not upload your repository or report to a hosted service.
- Exit code `0` means a trustworthy comparison completed without a confirmed blocking future regression, `1` means a confirmed future regression was found, and `2` means the analysis could not complete trustworthily. Baseline failures, flaky results, execution failures, and unsupported candidates are not silent compatibility passes.
- Version `1` supports .NET repositories, explicit NuGet prerelease package overrides, SDK previews, and runtime previews. Non-.NET ecosystems, hosted builds, update pull requests, scheduling, and automatic patch generation are outside this package.

## Documentation

Read the [full CompatRadar README](https://github.com/KeelMatrix/CompatRadar/blob/main/README.md) for the complete workflow and examples. The [compatibility documentation](https://github.com/KeelMatrix/CompatRadar/blob/main/docs/compatibility.md) defines the configuration, report, classification, and exit-code contracts. See [SECURITY.md](https://github.com/KeelMatrix/CompatRadar/blob/main/SECURITY.md) for execution and security guidance, [PRIVACY.md](https://github.com/KeelMatrix/CompatRadar/blob/main/PRIVACY.md) for telemetry and data handling, and the [troubleshooting section](https://github.com/KeelMatrix/CompatRadar/blob/main/README.md#troubleshooting) for common failures.

## License

CompatRadar is licensed under the MIT License. See the [license](https://github.com/KeelMatrix/CompatRadar/blob/main/LICENSE).
