# Technical validation gate

Run the reproducible gate from a clean checkout:

```powershell
$env:KEELMATRIX_NO_TELEMETRY = '1'
pwsh -NoProfile -File scripts/technical-validation-gate.ps1 -OutputPath artifacts/technical-validation-gate.md
```

The command restores and builds the solution, runs the complete deterministic corpus, reruns the additive-feed integration test, and writes both the developer-facing Markdown evidence and a machine-readable JSON record at `artifacts/technical-validation-gate.json`. It returns `0` only when every requested probe ran and passed. It returns `2` when the deterministic gate completed but an optional environment prerequisite was not available; the Markdown and JSON records identify each unavailable or skipped probe. A genuine restore, build, test, feed, or real-repository probe failure still fails closed with a nonzero error rather than being recorded as a pass.

The gate restores from the checked-in `NuGet.config`, runs the complete deterministic corpus, records SDK/runtime and restore/build/test elapsed time, and writes the Markdown/JSON evidence pair. The corpus covers equivalent stable states, planted package and SDK/runtime breakages, stable fail-A/fail-B, candidate fail-A/fail-B, flaky attempts, `pass → pass → fail → fail`, `pass → fail → pass`, missing preview `UNSUPPORTED`, report determinism, redaction, non-mutation, and cleanup.

The acceptance calculation is explicit: all six deterministic planted-breakage cases must be detected (100%, above the required 95%) and the equivalent-state corpus must produce zero `FUTURE_REGRESSION` results. Any disagreement is `INCONCLUSIVE_FLAKY`; a missing installed preview is `UNSUPPORTED`; neither can produce a clean exit code.

When a preview SDK and external repositories are available, pass the exact candidate and checked-out paths with repeated `-RealRepositoryPath` arguments. The script records those probes without modifying the source paths. Public CI provisions the real preview candidate and pinned real-repository corpus and runs the full form of the gate. The local command intentionally remains runnable without those optional inputs and records their absence rather than claiming those probes ran. An independent reviewer must assess report usefulness and diagnostic attribution before any release decision; this evidence is not a release authorization.
