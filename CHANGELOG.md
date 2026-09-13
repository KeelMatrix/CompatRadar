# Changelog

This file records user-visible changes to CompatRadar. The first release is not yet published.

## [Unreleased]

- Initial .NET tool for local and CI future-compatibility comparisons.
- Comparisons no longer expose candidate identity to the validation command, and they clear inherited MSBuild SDK/tool-path pinning and node reuse so stable and candidate states cannot share build state.
- Reproduction witnesses now record the deterministic content identity of the materialized state and whether the working tree was dirty, in addition to the Git revision.
- Console output claims a first-bad candidate only when the watch actually has a confirmed monotonic boundary; later monotonic failures and non-monotonic failures are labelled as observed failures.
- Runtime-preview selection now targets the exact installed `Microsoft.NETCore.App` runtime independently of SDK selection through candidate-only runtime framework/host overrides with roll-forward disabled; unavailable runtimes remain `UNSUPPORTED`.
- Runtime-preview selection no longer uses process-wide environment overrides, so nested .NET validation commands retain their own runtime configuration; passing and unevaluated witnesses leave `focusedFailure` empty.
- Added package-contract inspection, isolated consumer/Action smoke, compatibility policy, and tag-gated Trusted Publishing workflow.
- Candidate versions now follow NuGet version semantics instead of a hand-written ordinal parser: SemVer 2 build metadata is accepted and never changes precedence, prerelease labels compare case-insensitively, and NuGet-normalized equivalents such as `1.0` and `1.0.0` are reported as duplicates. This removes a mixed-case ordering path that could produce an incorrect first-bad candidate claim.
- An optional feed URL is now accepted only when it has no user-info, query string, or fragment, so a feed credential cannot be retained in a witness, JSON report, console output, `reproduce` output, diagnostic, or Action summary. Credential-bearing feeds use environment-based authentication or a NuGet credential provider.
- Package metadata now uses the approved `KeelMatrix` copyright and the approved SDK/runtime description, and package inspection fails closed when the copyright or description is missing, wrong, or case-mismatched.
