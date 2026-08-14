# ArcGIS Codex live bridge protocol v0.20

Protocol v0.20 adds 10 direct SDK actions, raising the direct action count from
184 to 194. The batch concentrates on three reusable surfaces that previously
required one-off Add-in changes: active view filters, editable map graphics, and
specialized LAS/voxel layers.

## Active view time, range and floors

- `get_view_filter_state` reads the active view's `TimeRange`, `RangeExtent`,
  available and active range names, and complete floor-filter state.
- `configure_view_filters` sets or clears time and numeric range extents,
  switches the active named range, and configures floor site/facility/level,
  vertical order, visibility mode and UI state.
- Date/time values use ISO-8601 strings and preserve explicit ArcGIS time-zone
  reference settings.

These are view filters only. They do not change fields, rows, layer time
definitions, floor-aware data models or source datasets.

## Editable map graphics

- `list_map_graphics` inventories graphics layers and their elements, including
  transform, bounds, locking, visibility, selection and optional complete CIM.
- `manage_map_graphics` updates transforms and state, reorders elements,
  selects/unselects them, and performs explicitly confirmed removal or clearing.
- `get_map_graphic_cim` and `set_map_graphic_cim` provide protected same-type
  CIM round trips for every concrete map graphic supported by ArcGIS Pro.
- MCP `live_patch_cim_object` now accepts `target_type=map_graphic`, so long-tail
  symbol, text and effect properties can use dry-run merge patches without
  another Add-in build.

The existing `draw_graphics` action remains the typed factory for point, line,
polygon, rectangle and text graphics.

## LAS dataset layers

- Inspect LAS dataset size, file/version counts, extent, class codes, returns,
  surface constraints, eye-dome lighting, selection and display filters.
- Configure eye-dome lighting, selectable classes, visible-point selection,
  active surface constraints and either preset or complete LAS display filters.
- Clear the in-memory LAS point selection without editing LAS files.

## Voxel layers

- Inspect variables, renderers, bounded distributions and statistics,
  isosurfaces, volumes, sections, slices, locked sections, unique times,
  lighting and container state.
- Configure volume/surface visualization, vertical exaggeration, cartographic
  offset, snapping, diffuse/specular lighting, selected variables and container
  visibility/expansion.
- Replace a variable's same-family native CIM voxel renderer and create, update
  or explicitly remove isosurfaces.

All v0.20 actions mutate only the in-memory project/view state. They do not save
the APRX or alter source GIS datasets.
