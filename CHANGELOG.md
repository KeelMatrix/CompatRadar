# Changelog

## 0.1.0 — Unreleased

- Initial .NET tool for local and CI future-compatibility comparisons.
- Comparisons no longer expose candidate identity to the validation command, and they clear inherited MSBuild SDK/tool-path pinning and node reuse so stable and candidate states cannot share build state.
- Reproduction witnesses now record the deterministic content identity of the materialized state and whether the working tree was dirty, in addition to the Git revision.
- Console output claims a first-bad candidate only when the watch actually has a confirmed monotonic boundary; later monotonic failures and non-monotonic failures are labelled as observed failures.
- Runtime-preview selection now targets the exact installed `Microsoft.NETCore.App` runtime independently of SDK selection through candidate-only runtime framework/host overrides with roll-forward disabled; unavailable runtimes remain `UNSUPPORTED`.
- Added package-contract inspection, isolated consumer/Action smoke, compatibility policy, and tag-gated Trusted Publishing workflow.
