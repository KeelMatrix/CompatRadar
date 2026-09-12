# Validation corpus

`scripts/validation-corpus.ps1` produces reproducible evidence that CompatRadar detects genuinely
incompatible candidate states and does not report regressions when stable and future states are
equivalent. Run it from a clean checkout:

```powershell
$env:KEELMATRIX_NO_TELEMETRY = '1'
pwsh -NoProfile -File scripts/validation-corpus.ps1 -OutputPath artifacts/validation-corpus.md
```

The script restores and builds the solution, runs the complete deterministic regression corpus,
reruns the additive-feed integration test, probes every pinned repository/candidate pair in
`scripts/validation-corpus.json` when a prerelease SDK/runtime is installed, and writes Markdown,
JSON, and witness samples under `artifacts/`. It returns `0` only when every named check
and computed detection condition passes. It returns `2` when an environment prerequisite is
unavailable; the generated records identify each unavailable or skipped probe. A genuine restore,
build, test, feed, probe, or cleanup failure stays nonzero and is never recorded as a pass.

## What the corpus proves

The deterministic regression corpus contains real incompatibilities rather than injected
signaling:

- incompatible dependency states are real package versions that remove the API the fixture calls,
  so the candidate build fails while the stable control still builds and runs;
- the incompatible runtime state is a candidate runtime the repository's declared runtime
  contract does not support;
- the incompatible SDK state is a candidate SDK the repository's declared SDK contract does not
  support;
- equivalent states, flaky repositories, stable failures, non-monotonic sequences, timeouts,
  bounded output, and secret-shaped diagnostics are covered by the same corpus.

Detection metrics are recomputed from the classifications the tests actually observed. Each test
records its classification for the cases declared in `scripts/validation-corpus.json`
(`expectedOutcomes`). The run computes the detected incompatible-state rate against the required
95% threshold and the number of equivalent states that reported `FUTURE_REGRESSION`, which must be
zero. A case that records no outcome fails the run instead of being counted as detected.

## Repository probes and witness samples

When a prerelease SDK/runtime and the pinned repositories are available, pass the exact candidates
and checked-out paths with repeated `-PreviewCandidate` and `-RealRepositoryPath` arguments. The
script maps each SDK candidate to its exact runtime candidate from `scripts/validation-corpus.json`,
verifies both are installed, copies each source into isolated temporary state, and never writes to
the source paths. Public CI provisions the pinned prerelease SDK/runtime candidates and the
complete corpus, then uploads the generated artifacts.

Each probe also writes a sample into `artifacts/validation-witness-pack/samples/sample-*.json`.
Those samples deliberately omit classifications, expectations, and every other outcome label.
`scripts/validate-witness-pack.ps1` checks structural completeness and the absence of labels; it
does not evaluate attribution or reproduction usefulness and does not read expectation data.
