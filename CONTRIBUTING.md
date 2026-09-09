# Contributing

Thank you for helping improve CompatRadar.

## Development

Install the .NET 8 SDK, then run restore, Release build, tests, format verification, and package smoke checks from the commands in `AGENTS.md`. Set `KEELMATRIX_NO_TELEMETRY=1` during development and tests.

Keep changes focused, add regression coverage for behavior changes, and update the README when the command or report contract changes. Do not add credentials, repository contents, local telemetry files, or generated build output.

## Pull requests

Describe the user-facing behavior, tests run, package/consumer verification, and any platform or network evidence that was unavailable. Security fixes should follow `SECURITY.md`.
