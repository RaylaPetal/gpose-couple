# Live GPose failure investigation — 2026-09-27

User report: posing the partner's character and repeatedly pressing Push pose causes no visible
change on the partner, even after five minutes. Leaving and re-entering GPose exposes the last pose.
The supplied screenshot shows historical receives/applies and an over-budget warning; it is outside
GPose when captured, so it cannot identify the precise live readiness state.

Inspected the local installed Lightless Sync 3.3.0.0 assembly, SimpleHeels 0.11.1.12 API, Player Sync
1.15.5.7 receive path, local plugin log, and matching upstream Lightless source. Lightless defers its
player reconciliation in GPose, including SimpleHeels. The local log repeatedly shows remote tag
activity just after GPose exits. The design's assumption that Lightless inherits Player Sync's separate
optional-plugin fast path is invalid for this installed version. The 16 KB warning is not a publish
rejection in CoPose and does not account for this GPose gate.

Implemented CoPose retry/cancellation handling, actor write serialization and successful-acknowledgement
statistics. Separated tag-size warnings from apply errors so the warning cannot overwrite them.
Added regressions explicitly posing the partner's character and recovering without another tag or
GPose restart. CoPose 0.2.3.0 builds and all 74 tests pass.

The user ruled out replacing Lightless. The CoPose release contains no Lightless patch or binary.
The dependency's GPose gate remains unresolved by this release; a supported integration or upstream
correction is needed.

No two-player verification or server size/rate measurement is claimed. Existing in-game verification
tasks remain open. The user requested a CoPose release to test the changes.
