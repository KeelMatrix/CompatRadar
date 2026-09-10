# Compatibility and change policy

`compat-radar.json` schema version `1` and the JSON report schema version `1` are durable v1 contracts.

- Additive fields and result metadata are allowed when existing readers can ignore them. Existing field meanings, command names, exit codes, and classification strings are not changed in place.
- A breaking schema, CLI, or classification change requires a new integer schema version, migration notes, a changelog entry, updated README/help text, and an explicit compatibility decision before release.
- Deprecated fields remain accepted for at least one minor release when practical. They are documented, covered by a deprecation test, and removed only with a schema/version decision.
- Configuration parsing is fail-closed for unknown or malformed values. Reports are deterministic for equivalent inputs and never contain raw command output or secrets.
- `FUTURE_REGRESSION` requires a passing stable control and repeated non-zero candidate attempts with the same normalized failure signature and fingerprint. Different non-zero exit codes or diagnostics are `INCONCLUSIVE_FLAKY`.
- Every contract change adds a round-trip test and updates the committed synthetic v1 fixtures under `tests/fixtures/v1/`. Golden fixtures are not rewritten silently; changes require a test and changelog explanation.
- New durable result classifications must define their exit-code behavior and have a reachable engine test. `UNSUPPORTED` means the selected channel cannot be exercised in the current environment and is an exit-code `2` outcome.
- Release notes must describe user-visible CLI, configuration, report, Action, or packaging changes. Internal refactors need no contract note unless serialized output changes.

The compatibility suite loads the v1 configuration fixture, deserializes the v1 report fixture, serializes it again, and verifies that required contract fields survive the round trip.
