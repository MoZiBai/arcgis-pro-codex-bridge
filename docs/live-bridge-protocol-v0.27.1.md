# ArcGIS Codex live bridge protocol v0.27.1

Protocol v0.27.1 is a runtime correctness patch over v0.27.0 with the same 236
actions.

## Fix

`manage_exploratory_analysis` no longer calls `GetAwaiter().GetResult()` on
`AddExploratoryAnalysisAsync` or `RemoveExploratoryAnalysisAsync` while already
inside `QueuedTask`. Add/remove/clear now await the SDK operations asynchronously
on the queued task, preventing a main-thread deadlock observed while clearing
multiple temporary analyses.

## Safety and compatibility

- No action was added or removed; the protocol still exposes 236 actions.
- Source datasets remain unchanged and the project is never saved implicitly.
- v0.27 validation continues to use only temporary project objects and synthetic
  outputs, then verifies cleanup and unchanged APRX/source fingerprints.
- Full v0.27.0 feature details remain in
  [`live-bridge-protocol-v0.27.0.md`](live-bridge-protocol-v0.27.0.md).
