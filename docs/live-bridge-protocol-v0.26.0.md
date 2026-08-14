# ArcGIS Codex live bridge protocol v0.26.0

Protocol v0.26.0 increases direct ArcGIS Pro SDK actions from 221 to 228 and
adds broad reusable public-API surfaces discovered through the installed 3.6.3
SDK assemblies.

## New actions

- `get_portal_state`: read-only portal URI, active/available/signed-in state and
  username; credentials and tokens are never returned.
- `inspect_time_zones`: bounded IANA catalog search and IANA/Windows ID conversion.
- `inspect_locators`: active-view locator provider inventory.
- `geocode_location`: read-only locator query with no map result insertion or zoom.
- `get_operation_history`: bounded map/layout/report/presentation undo/redo history.
- `control_operation_history`: confirmed, source-editing-gated undo/redo control.
- `geometry_engine`: 39 in-memory geometry operations covering projection,
  buffering, overlay, predicates, measurements, simplification, densification,
  affine transforms, nearest point, and multipart splitting.

## SDK discovery improvements

- Public SDK search now excludes namespaces containing `.Internal.`.
- Method/property signatures show whether a member is static.
- `inspect-arcgis-sdk.ps1` supports `-OnlyMatchingMembers` for concise signature
  discovery.
- The corrected local inventory finds 4,482 non-internal public types across 16
  ArcGIS Pro assemblies; the entry-point comparison is explicitly a triage signal,
  not a claim that every type should receive a dedicated wrapper.

## Safety

- Geometry execution is memory-only, accepts at most 2 MB per geometry input, and
  reports that no dataset or project was changed.
- Portal inspection never calls token or credential APIs.
- Geocoding may contact configured locator services but never adds results to the
  map and reports `external_state_modified=false`.
- General undo/redo is disabled unless the existing source-editing gate is enabled
  and `confirm_operation=true` is supplied.
