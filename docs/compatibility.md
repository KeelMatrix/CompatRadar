# Compatibility and change policy

`compat-radar.json` schema version `1` and the JSON report schema version `1` are durable v1 contracts.

- Additive fields and result metadata are allowed when existing readers can ignore them. Existing field meanings, command names, exit codes, and classification strings are not changed in place.
- A breaking schema, CLI, or classification change requires a new integer schema version, migration notes, a changelog entry, updated README/help text, and an explicit compatibility decision before release.
- Deprecated fields remain accepted for at least one minor release when practical. They are documented, covered by a deprecation test, and removed only with a schema/version decision.
- Configuration parsing is fail-closed for unknown or malformed values. Reports are deterministic for equivalent inputs and never contain raw command output or secrets.
- Candidate versions are validated, deduplicated, and ordered with NuGet version precedence. SemVer 2 build metadata is accepted and precedence-neutral, prerelease labels compare case-insensitively, and NuGet-normalized equivalents such as `1.0` and `1.0.0` are treated as duplicates. The configured candidate string is preserved for reporting, but ordering and first-bad boundaries never use culture-sensitive or ordinal string comparison.
- An optional feed is accepted only as an absolute HTTP(S) URL with no user-info, query string, or fragment. Accepted feed URLs are retained verbatim in witnesses, JSON reports, console output, and Action summaries, so schema `1` rejects any component that could carry a credential under a name the tool cannot recognize rather than redacting it on each serialization path. Feed rejection reports the watch index only and never echoes the rejected value; credential-bearing feeds must use environment-based authentication or a NuGet credential provider.
- `FUTURE_REGRESSION` requires a passing stable control and repeated non-zero candidate attempts with the same normalized failure signature and fingerprint. Different non-zero exit codes or diagnostics are `INCONCLUSIVE_FLAKY`.
- Runtime-preview witnesses record the exact requested runtime in the additive `runtime` field and candidate input configuration. Runtime candidates are selected independently of SDK selection; SDK preview candidates continue to use isolated `global.json` SDK overrides.
- Witnesses additively record `repositoryContentHash`, a deterministic identity of exactly the content a comparison materialized, and `repositoryWorktreeDirty`, which reports whether the working tree contained uncommitted or untracked content at analysis time. Both fields are additive; existing readers can ignore them.
- Every contract change adds a round-trip test and updates the committed synthetic v1 fixtures under `tests/fixtures/v1/`. Golden fixtures are not rewritten silently; changes require a test and changelog explanation.
- New durable result classifications must define their exit-code behavior and have a reachable engine test. `UNSUPPORTED` means the selected channel cannot be exercised in the current environment and is an exit-code `2` outcome.
- Release notes must describe user-visible CLI, configuration, report, Action, or packaging changes. Internal refactors need no contract note unless serialized output changes.

The compatibility suite loads the v1 configuration fixture, deserializes the v1 report fixture, serializes it again, and verifies that required contract fields survive the round trip.
