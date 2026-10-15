# ArcGIS Codex live bridge protocol v0.30.0

Target: ArcGIS Pro 3.6 on Windows x64. The MCP server and Add-in must be upgraded
together. The signed queue retains the `hmac-sha256-v1` envelope from v0.29.0;
the new version identifies the expanded action set. It is separate from the
negotiated MCP protocol, which remains `2024-11-05`.

## Project creation

`live_create_blank_project` dispatches `create_blank_project` through the public
`Project.CreateAsync(CreateProjectSettings)` SDK API with the `Untitled`
template. It is available even when no project is loaded. `output_aprx` is
required, normalized under the configured output root, and means the exact
final `.aprx` path. The bridge verifies that the current project has that path
and the saved file exists before reporting success.

The SDK first materializes an untitled project inside a sibling support folder,
saves the optional map/layout/map-frame/data structure, then calls
`SaveAsAsync` for the requested file. Results report the support folder, home
folder, default GDB and toolbox. Source datasets are referenced, not edited.
Blank creation does not automatically restore the previously open project
after a failure.

The out-of-process `create_project_from_template` tool copies a template into
a staging `.aprx`, initializes it, saves it, and then promotes it to the output
path. An existing target requires explicit `overwrite` and retains a
`.previous` backup. It reports before/after inventory and changes without
modifying the template or source datasets.

## Native legends and layout checks

Native map-surround add/configure actions support `layer_names`, `legend_order`
and item display options. Legend items stay linked to their layers. Explicit
layer requests disable automatic additions, reject missing layers, and filter
items after ArcGIS Pro has populated them. The default background is
transparent.

`live_validate_layout` dispatches `validate_layout`. It returns page dimensions,
native element bounds, issue counts, required surround checks and legend item
counts. `live_export_layout` optionally runs the check using
`validate_before_export=true` and blocks reported errors unless
`allow_invalid_export=true` is explicitly supplied. Validation defaults to off.

This is a basic geometry checker. Point text and external ticks can report
false zero-size errors. Bounding boxes do not distinguish intended overlaps
from obscured map content. Leader-line intersections, visual balance, automatic
layout planning and automatic repair are not provided.

## MCP jobs and diagnostics

`arcgis_job_status`, `arcgis_job_history` and `arcgis_cancel_job` expose process
job records keyed by JSON-RPC request ID. Requests with `_meta.progressToken`
can receive `notifications/progress`. Tool results include job ID and status
metadata. Cancellation is cooperative and requires `confirm_cancel=true` when
using the dedicated cancellation tool. History is process-local, with a target
retention of 200 records; it is cleared on restart. Progress is phase-based,
not an ArcGIS-native percentage or ETA. Artifact inventory is limited to the
configured output root.

The probe negotiates a supported MCP version, sends the initialized notification,
handles progress notifications and tool-list pagination, validates response IDs
and tool errors, drains stderr, and applies per-request deadlines.

## Safety and compatibility

HMAC signatures, private Windows ACLs, path restrictions and independent safety
gates remain in effect. Public errors redact absolute paths; detailed Add-in
exceptions require explicit debug configuration. Tool failures use MCP
`isError=true`; cancelled requests use JSON-RPC cancellation errors. The runtime
rejects mismatched Add-in protocol versions.

See [release notes](release-v0.30.0.md) for installation and known limitations.
