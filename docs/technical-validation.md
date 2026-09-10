# Technical validation gate

Run the reproducible gate from a clean checkout:

```powershell
$env:KEELMATRIX_NO_TELEMETRY = '1'
pwsh -NoProfile -File scripts/technical-validation-gate.ps1 -OutputPath artifacts/technical-validation-gate.md
```

The gate restores from the checked-in `NuGet.config`, runs the complete deterministic corpus, records SDK/runtime and restore/test elapsed time, and writes a developer-facing evidence artifact. The corpus covers equivalent stable states, planted package and SDK/runtime breakages, stable fail-A/fail-B, candidate fail-A/fail-B, flaky attempts, `pass → pass → fail → fail`, `pass → fail → pass`, missing preview `UNSUPPORTED`, report determinism, redaction, non-mutation, and cleanup.

The acceptance calculation is explicit: all six deterministic planted-breakage cases must be detected (100%, above the required 95%) and the equivalent-state corpus must produce zero `FUTURE_REGRESSION` results. Any disagreement is `INCONCLUSIVE_FLAKY`; a missing installed preview is `UNSUPPORTED`; neither can produce a clean exit code.

When preview SDKs and external real repositories are available, pass their checked-out paths with repeated `-RealRepositoryPath` arguments. The script records those paths without modifying them. The local gate records the absence of an installed preview or external corpus rather than claiming those probes ran. An independent reviewer must assess report usefulness and diagnostic attribution before any release decision; this evidence is not a release authorization.
