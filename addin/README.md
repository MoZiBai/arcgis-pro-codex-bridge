# ArcGIS Codex Bridge Add-in

This is the auto-loaded ArcGIS Pro 3.6 bridge. The user opens ArcGIS Pro and a
project; the add-in then processes the existing MCP file queue automatically.
No Python Toolbox start step and no mouse/keyboard automation are required.

Bridge protocol v0.29.0 supports:

- detecting the current ArcGIS Pro project and maps;
- adding datasets to the current/selected map;
- visibility, transparency, ordering, definition queries, and view navigation;
- layer inspection, renaming, duplication, grouping, and blending modes;
- parameterized vector renderers and raster colorizers;
- labels and editable map graphics;
- parameterized layouts, editable elements, external coordinate ticks, and
  manual color bars and renderer-derived raster color bars;
- non-destructive elevation-tint plus physical-hillshade terrain stacks;
- ordered batch workflows and a capability handshake;
- displaying outputs produced by `live_run_geoprocessing_tool` (geoprocessing
  runs through the tested ArcPy worker, then the add-in loads its outputs).
- project inventory and explicit project save/save-as;
- map lifecycle, camera, bookmark, selection, schema, and bounded query actions;
- standalone table management and bounded table queries;
- layout lifecycle, multiple map frames, pictures, grouping, and map series;
- project-item metadata and existing-report management/export.
- Catalog path items, broken-reference repair, styles, and favorites;
- presentations with blank/map/image/video pages, page configuration, export,
  and editable PRSX save;
- attachment inventory/export, gated attachment edits, and bounded relationship
  traversal.
- native style-based map grids and independent map-surround lifecycle,
  configuration, style application, and CIM access.
- typed data-connection inspection, confirmed connection replacement, and
  workspace-level data-source replacement;
- bounded spatial queries and spatial selection using inline geometry or a
  reference feature layer.
- native dynamic table/chart frames, multiple label classes, report creation
  and section configuration, and typed native-grid interval/label/tick helpers.
- generic ArcGIS Pro Framework state and command inspection, plus separately
  gated exact-ID command, tab, tool, backstage-page, and pane control for
  configuration-driven coverage of long-tail Pro UI capabilities.
- a bounded `run_arcpy_worker` host action that starts only the installed
  `worker.py` under ArcGIS Pro's own Python when Windows security blocks a
  Codex-child ArcPy process; it does not add antivirus exclusions.

The exact command contract and safety behavior are documented in
`docs/live-bridge-protocol-v0.29.0.md`. Source datasets remain read-only; layer
removal affects the map only. Saving a project copy is an explicit Save As
operation and switches ArcGIS Pro to the new copy.

Build output is an `.esriAddinX` package. Use `scripts/build-addin.ps1`, then
`scripts/install-addin.ps1` for the one-time local installation.

Current package path:

`addin/ArcGISCodexBridgeAddin/bin/x64/Release/ArcGISCodexBridgeAddin.esriAddinX`

After installation, restart ArcGIS Pro. The module has `autoLoad="true"`, so
there is no bridge button or toolbox to start on later sessions.
