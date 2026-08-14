# ArcGIS Codex live bridge protocol v0.24.0

Protocol v0.24.0 raises the direct ArcGIS Pro SDK action count from 219 to 221
and adds a second broad discovery layer for installed geoprocessing tools.

## New typed live actions

- `transform_view_coordinates` converts points between map, view-client and
  desktop-screen coordinate spaces using the active native `MapView`.
- `manage_layout_guides` inspects, adds, replaces, removes and clears native
  horizontal/vertical `CIMGuide` entries on a layout page.

Layout and active-map-view export now support EPS and AIX in addition to PDF,
PNG, JPEG, TIFF, SVG and SVGZ. EPS/AIX-specific font, rasterization, marker and
vector-compression options are exposed through the existing export actions.

## Installed geoprocessing catalog

Two MCP-only tools discover the local ArcGIS Pro ArcPy surface without adding
one compiled Add-in wrapper per geoprocessing operation:

- `search_arcgis_geoprocessing_registry`
- `describe_arcgis_geoprocessing_tool`

On the audited ArcGIS Pro 3.6.3 installation the catalog contains 1,977 tools.
Search results include canonical ArcPy paths, legacy names, signatures,
summaries and policy hints. Tool inspection includes localized parameter names,
input/output direction, required/optional status, datatype, defaults, filters
and dependencies.

The generic runner now blocks schema-changing and known in-place source-data
mutation tools through their independent default-off safety gates. This closes
a path where callers could previously bypass the dedicated schema/source-edit
routes by naming the same ArcPy operation through the general runner.

## Runtime hardening

Feature popup creation and closing are dispatched to the ArcGIS Pro UI thread.
The v0.23.2 TOC, Framework pane, raster, animation, finite-number and elevation
error protections remain in force. No new action saves the open project
implicitly.
