# Security Policy

## Reporting a Vulnerability

Please do not open a public issue for a suspected security vulnerability. Email `keelmatrix@gmail.com` with the affected package version or commit, environment, impact, concise reproduction steps, and sanitized logs. Do not include credentials or private repository contents.

CompatRadar executes the validation command configured by the repository owner. It is not a sandbox. Run it only in an environment where the repository commands and credentials are trusted.

The tool keeps comparison state in a temporary directory, bounds process time and captured output, terminates timed-out process trees, avoids following reparse points, and sanitizes diagnostics. Reports stay local unless the caller explicitly uploads them as CI artifacts.

## Supported Versions

CompatRadar has no published release yet, so no package version is currently supported. During pre-release development, security fixes are applied to the `main` branch. After the first release, the latest published version on the `0.x` line will be the supported baseline; older preview builds and unreleased commits will not be supported security baselines. The maintainers will acknowledge private reports within five business days and will publish a fix or mitigation timeline when the impact is confirmed.
