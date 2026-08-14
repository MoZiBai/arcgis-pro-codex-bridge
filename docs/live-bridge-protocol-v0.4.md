# ArcGIS Codex live bridge protocol v0.4

The bridge is a local, file-queue RPC layer between the Codex MCP server and an
auto-loaded ArcGIS Pro SDK add-in. The user opens ArcGIS Pro and a project; all
map, symbology, graphics, and layout operations use the official SDK rather than
mouse or keyboard automation.

## Safety model

- Source datasets are read-only by default.
- Geoprocessing outputs, exports, and project copies must be under the configured
  output root.
- Removing a layer removes it from the map only; it never deletes its dataset.
- Destructive ArcPy tools remain disabled by the MCP server.
- `save_project_as` is explicit and reports that ArcGIS Pro switches to the new
  project copy. No action silently saves the original project.

## Capability handshake

Call `live_capabilities` before depending on a live action. The add-in returns
its protocol version and exact supported action list. `live_bridge_status`
returns the same information with detailed map, layer, layout, and element state.

## Core action families

### In-process geoprocessing

`run_geoprocessing` uses ArcGIS Pro SDK `Geoprocessing.ExecuteToolAsync` inside
the open Pro process. It can add outputs directly to the active project and does
not depend on mouse automation or an external ArcPy process. Destructive tool
names remain blocked by the MCP server.

### Layer and view management

- `manage_layers`: visibility, transparency, scale thresholds, definition query.
- `manage_layers`: also supports layer renaming and ArcGIS Pro blending modes.
- `describe_layer`: returns the data source, blend mode, renderer/colorizer and
  optional raw CIM JSON for advanced inspection.
- `duplicate_layer` and `create_group_layer`: build non-destructive layer stacks
  without copying source datasets.
- `remove_layers`: remove from the map without deleting source data.
- `reorder_layer`: top, bottom, or root map index.
- `zoom_to_layer`: zoom the active map view with a scale factor.

### Symbology and labeling

- `apply_vector_symbology`: simple, unique value, and graduated colors.
- `apply_raster_symbology`: stretch, classify, discrete, and shaded relief.
- Both accept custom RGB/hex color stops and transparency.
- `configure_labels`: label expression/field, filter, text symbol, visibility.

### Map graphics

`draw_graphics` creates editable point, line, polygon, rectangle, and text
elements inside a named graphics layer. Coordinates use the active map spatial
reference.

### Layouts

- `create_layout`: parameterized page, map frame, title, legend, north arrow,
  scale bar, external coordinate ticks, and classified color bar.
- `update_layout_element`: move, resize, rotate, show/hide, or explicitly delete.
- `add_layout_graphics`: text, lines, and rectangles.
- `add_layout_ticks`: external ticks and coordinate labels only; no internal grid.
- `add_layout_colorbar`: manual horizontal or vertical color bar.
- `add_raster_colorbar_from_layer`: reads the actual raster colorizer and creates
  a renderer-derived color bar. Classified widths are proportional to the real
  class values; continuous ramps preserve multipart ramp weights.
- `set_layout_text`, `zoom_layout`, `export_layout`, and `import_layout`.

### Ordered workflows

`execute_batch` accepts up to 100 supported actions and executes them in order in
one bridge queue job. It is the preferred path for complete cartographic jobs
such as “hide every layer, add a DEM, style it, zoom, build a layout, and export”.

`live_create_terrain_stack` is a higher-level MCP workflow. It reads the current
DEM layer path, creates a derived hillshade under the configured output root,
adds it to the visible map, applies Multiply blending, and changes the source DEM
layer to elevation tint only. The source raster is never edited.

## Color format

Colors may be hex strings (`#2E6B4F`, `#2E6B4FCC`) or arrays
`[red, green, blue, alphaPercent]`. Multi-stop ramps are supplied as
`color_stops` in low-to-high order.

Color stops may also be objects with explicit values, for example
`{"value": 1200, "color": "#E3AF63"}`. Multipart ramp weights then follow the
numeric intervals rather than treating every segment as equally wide.
