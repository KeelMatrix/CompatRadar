# Changelog

This file records user-visible changes to CompatRadar.

## [0.1.1] - 2026-09-23

### Changed

* Updated the `KeelMatrix.Telemetry` package dependency to `0.1.1`.
* Hardened the compatibility validation corpus and release checks so pinned repository checks run against the intended test and SDK/runtime setup without modifying the product working tree.

## [0.1.0] - 2026-09-15

### Added

* Initial `compat-radar` .NET tool for testing explicitly selected NuGet prereleases and installed .NET SDK/runtime previews against a repository's current stable state, using isolated temporary copies instead of mutating the active worktree.
* Trustworthy comparison semantics that run the stable control first, confirm candidate failures across repeated runs, distinguish confirmed future regressions from baseline failures, flakiness, execution failures, and unsupported candidates, and expose stable exit codes for local and CI use.
* First-bad localization for ordered candidates only when the evidence establishes a confirmed monotonic boundary; non-monotonic failures are reported without making a false first-bad claim.
* Versioned, deterministic JSON reports and local reproduction witnesses containing the tested repository revision and content identity, comparison evidence, and sanitized diagnostics without requiring a hosted reporting service.
* Versioned configuration for NuGet prerelease, SDK preview, and runtime preview watches, with NuGet-compatible candidate ordering and duplicate detection, bounded validation timeouts and confirmation runs, and credential-safe optional feed handling.
* GitHub Actions integration using the same comparison engine, with job-summary output, a structured report artifact path, and compatibility results suitable for CI gating.
* Local-first security and privacy behavior with bounded child processes and captured output, isolated comparison state, no repository or report upload, and best-effort KeelMatrix telemetry only after a trustworthy comparison. Telemetry supports `KEELMATRIX_NO_TELEMETRY=1` opt-out and excludes repository identity, package or candidate choices, commands, tests, logs, URLs, secrets, and report contents.
* .NET 8 tool support for Windows, Linux, and macOS.
