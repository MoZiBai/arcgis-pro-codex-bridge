# ArcGIS Codex live bridge protocol v0.27.3

Protocol v0.27.3 is a report-coordinate correctness patch over v0.27.2 with the
same 236 actions.

## Fix

`manage_report_elements` now validates element envelopes against each writable
report section's actual page coordinates (`GetX`, `GetY`, width, and height).
ArcGIS Pro report factories use report-canvas coordinates rather than coordinates
local to the selected section.

When `bounds` is omitted, the bridge now derives a finite default envelope inside
the selected section's real extent. Explicit out-of-section bounds return an error
that includes both the section extent and the received envelope.

## Safety and compatibility

- No action was added or removed; the protocol still exposes 236 actions.
- The fix affects only unsaved report elements and never edits source datasets.
- v0.27 runtime validation omits explicit report bounds so it exercises the safe
  section-aware default placement.
- Root-section selection details are documented in
  [`live-bridge-protocol-v0.27.2.md`](live-bridge-protocol-v0.27.2.md).
