# Security

## Reporting a vulnerability

Please do not open a public issue for a suspected security vulnerability. Email `keelmatrix@gmail.com` with a concise description, reproduction steps, and the affected version. Do not include credentials or private repository contents.

CompatRadar executes the validation command configured by the repository owner. It is not a sandbox. Run it only in an environment where the repository commands and credentials are trusted.

The tool keeps comparison state in a temporary directory, bounds process time and captured output, terminates timed-out process trees, avoids following reparse points, and sanitizes diagnostics. Reports stay local unless the caller explicitly uploads them as CI artifacts.
