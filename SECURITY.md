# Security

## Reporting a vulnerability

Please do not open a public issue for a suspected security vulnerability. Email `keelmatrix@gmail.com` with a concise description, reproduction steps, and the affected version. Do not include credentials or private repository contents.

CompatRadar executes the validation command configured by the repository owner. It is not a sandbox. Run it only in an environment where the repository commands and credentials are trusted.

The tool keeps comparison state in a temporary directory, bounds process time and captured output, terminates timed-out process trees, avoids following reparse points, and sanitizes diagnostics. Reports stay local unless the caller explicitly uploads them as CI artifacts.

## Supported versions and fixes

The latest published release on the `0.x` line is supported. Security fixes are backported only to the latest supported minor release; users should update to the newest patch release before requesting a backport. Unreleased commits and old preview builds are not supported security baselines. The maintainers will acknowledge private reports within five business days and will publish a fix or mitigation timeline when the impact is confirmed.
