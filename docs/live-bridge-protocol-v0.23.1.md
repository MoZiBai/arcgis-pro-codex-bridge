# ArcGIS Codex live bridge protocol v0.23.1

Protocol v0.23.1 is a runtime-safety hotfix for the 219-action v0.23 bridge. It
does not add or remove direct actions.

## UI-thread correctness

Framework application, command and pane inspection/control now executes on the
ArcGIS Pro WPF dispatcher. Map-graphic inventory no longer reads the
GUI-thread-only `IsExpandedInTOC` property from the mapping worker thread.

## JSON-safe view state

Active-view camera and view-size values are normalized so unset native values
such as positive or negative infinity are returned as `null` instead of causing
the whole bridge response to fail JSON serialization.

## Raster safety

Ad-hoc `CIMRenderingRule` construction from only a function name and arguments
is removed. A rendering rule can now be inspected or removed, or replaced only
from a complete, previously inspected CIM JSON object with explicit
confirmation. The replacement must contain non-empty `Name` and `Definition`
values and is limited to 1 MB.

An ordinary raster is rejected before a multidimensional active variable or
slice is assigned. This prevents invalid project state and native crashes from
incompatible layer definitions. Physical hillshade workflows should continue to
use the non-destructive derived hillshade terrain stack.

## Elevation query resilience

Elevation sampling and profile calls now report bounded per-request errors when
points are outside a usable surface or ArcGIS returns a mapping/COM failure.
One missing sample no longer fails the entire bridge job.

All existing source-editing, destructive, schema, publishing, Workflow and
Framework UI gates remain unchanged and disabled by default.

The MCP-only `reload_arcgis_mcp_policy` helper can explicitly re-read those
safety gates and exact Framework command/pane/tab/tool/Backstage allowlists from
the existing configuration. It does not hot-reload data roots, output paths,
Python locations or queue paths, and requires `confirm_reload=true`. This lets a
reviewed Framework ID be enabled without restarting ArcGIS Pro or the MCP
process.
