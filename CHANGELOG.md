# Changelog

## 0.1.0 — Unreleased

- Initial .NET tool for local and CI future-compatibility comparisons.
- Runtime-preview selection now targets the exact installed `Microsoft.NETCore.App` runtime independently of SDK selection through candidate-only runtime framework/host overrides with roll-forward disabled; unavailable runtimes remain `UNSUPPORTED`.
- Added package-contract inspection, isolated consumer/Action smoke, compatibility policy, and tag-gated Trusted Publishing workflow.
