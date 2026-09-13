# Privacy

CompatRadar performs comparisons locally or in the caller's CI environment. It does not upload source code, reports, commands, test names, package choices, versions, logs, stack traces, paths, or credentials to KeelMatrix.

After the first trustworthy stable-versus-future comparison, the tool may use `KeelMatrix.Telemetry` for a minimal pseudonymous activation and at-most-weekly heartbeat. Telemetry is best-effort and never affects the result. Set `KEELMATRIX_NO_TELEMETRY=1` to opt out.

KeelMatrix development, test, and CI runs should set the opt-out variable. Reports are local files or explicit CI artifacts selected by the repository owner.

The shared telemetry package maintains the common event and opt-out behavior; see [KeelMatrix.Telemetry](https://github.com/KeelMatrix/Telemetry). This document defines CompatRadar's product-specific activation and data boundary.
