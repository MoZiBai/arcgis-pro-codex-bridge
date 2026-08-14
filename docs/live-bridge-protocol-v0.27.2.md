# ArcGIS Codex live bridge protocol v0.27.2

Protocol v0.27.2 is a runtime correctness patch over v0.27.1 with the same 236
actions.

## Fix

`manage_report_elements` now distinguishes the root `ReportSection` container
from writable child sections such as `ReportHeader`, `ReportDetails`, page
headers, and footers. When `section_name` is omitted, the bridge prefers the
report header and then the tallest writable child section instead of passing the
invalid root container to `ReportElementFactory`.

The action now returns writable section names, types, widths, and heights during
inspection. It also validates element bounds against the selected child section
and reports the usable section inventory when a caller selects the root.

## Safety and compatibility

- No action was added or removed; the protocol still exposes 236 actions.
- The fix changes only unsaved project objects and does not edit source datasets.
- Runtime probes create temporary maps/reports from synthetic test data and
  remove them immediately without saving the APRX.
- Full v0.27.0 feature details remain in
  [`live-bridge-protocol-v0.27.0.md`](live-bridge-protocol-v0.27.0.md); the prior
  exploratory-analysis deadlock fix is documented in
  [`live-bridge-protocol-v0.27.1.md`](live-bridge-protocol-v0.27.1.md).
