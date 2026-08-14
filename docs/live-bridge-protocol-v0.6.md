# ArcGIS Codex live bridge protocol v0.6

Protocol v0.6 is a local file-queue RPC layer between the Codex MCP server and
an auto-loaded ArcGIS Pro 3.6 SDK add-in. The user opens ArcGIS Pro and a
project; operations then use the official SDK in process rather than mouse and
keyboard automation.

The add-in advertises the exact action set through `live_capabilities`. The MCP
server exposes matching `live_*` tools and validates paths and dangerous
parameters before a queue job is created.

## Safety model

- Source datasets are read-only by default.
- Derived geoprocessing outputs, exports, pictures, templates, copied projects,
  and report PDFs are restricted to configured roots.
- Layer and standalone-table removal only removes project references.
- Map, layout, report, bookmark, and project-item removal require purpose-built
  actions; map/layout/report removal requires an explicit confirmation flag.
- Destructive ArcPy tools remain blocked by the MCP server.
- Source feature editing and source metadata writing are not enabled by v0.6.
- `save_project` and `save_project_as` are explicit; no workflow silently saves
  the original project.
- External publishing and sharing are not enabled by default.

## Action families

### Project and metadata

- `status`, `capabilities`, `project_inventory`
- `save_project`, `save_project_as`, `save_copy`
- `get_metadata`: bounded XML for a project, map, layer, layout, or report
- `set_metadata`: project/map/layout/report only, with explicit confirmation;
  layer metadata writes are blocked because they may reach source metadata

### Maps, views, cameras, and bookmarks

- `create_map`, `rename_map`, `duplicate_map`, `remove_map`, `open_map`
- `get_camera`, `set_camera`, `zoom_full_extent`, `zoom_to_layer`
- `list_bookmarks`, `create_bookmark`, `remove_bookmark`, `zoom_to_bookmark`

### Layers, standalone tables, and queries

- `add_data`, `describe_layer`, `duplicate_layer`, `create_group_layer`
- `manage_layers`, `remove_layers`, `reorder_layer`
- `add_standalone_table`, `manage_standalone_table`,
  `remove_standalone_table`
- `get_layer_schema`, `query_features`, `summarize_field`
- `get_table_schema`, `query_table`
- `select_features`, `get_selection`, `clear_selection`

All feature and table queries are read-only and bounded. Query responses state
the total match count, returned count, and whether the result was truncated.
Geometry output is an optional summary rather than unbounded feature JSON.

### Symbology, labeling, and graphics

- `apply_symbology`, `apply_vector_symbology`, `apply_raster_symbology`
- `configure_labels`
- `draw_graphics`

The current typed renderers cover simple, unique-value, graduated-color,
stretch, classified, discrete, and shaded-relief workflows. Raw layer CIM can
still be inspected through `describe_layer` for unsupported renderer families.

### Geoprocessing

`run_geoprocessing` calls `Geoprocessing.ExecuteToolAsync` in the open Pro
process. It is the broad adapter for specialized toolboxes that do not expose a
public .NET API. The MCP server validates the toolbox and blocks destructive
tools unless the local safety configuration explicitly changes.

### Layouts and map series

- `create_layout`, `duplicate_layout`, `rename_layout`, `remove_layout`,
  `open_layout`
- `add_map_frame`, `configure_map_frame`, `zoom_layout`
- `create_map_series`: spatial, bookmark, or thematic
- `update_layout_element`, `organize_layout_elements`
- `add_layout_picture`, `add_layout_graphics`, `add_layout_ticks`
- `add_layout_colorbar`, `add_raster_colorbar_from_layer`
- `import_layout`, `set_layout_text`
- `export_layout`: PDF, PNG, JPEG, TIFF, SVG, or SVGZ

Layout elements remain native editable ArcGIS Pro elements. Grouping and
drawing-order actions operate on named elements. Picture elements reference an
allowed local file. Map-frame configuration can switch maps, use a layer or
bookmark for its camera, set rotation/aspect locking, and configure the frame
border.

### Reports

- `list_reports`, `duplicate_report`, `rename_report`, `remove_report`
- `export_report_pdf`

v0.6 manages existing reports. Creating a report from an arbitrary data source
is reserved for a later typed schema because report fields, sections,
statistics, related reports, and templates need a larger validated contract.

### Ordered workflows

`execute_batch` accepts up to 100 supported actions and executes them in order
inside one queue job. Nested batches are rejected. Each result records action,
index, success, and error details when applicable.

`live_create_terrain_stack` remains an MCP-level composite workflow that creates
a derived hillshade under the output root, adds it to the visible map, applies
Multiply blending, and styles the source DEM as elevation tint without editing
the source raster.

## Capability accounting

The v0.6 source contract contains 70 direct live actions. The contract test
checks that every direct `live_*` MCP tool maps to an add-in action, every
advertised action has a tool, tool names are unique, and no direct live tool is
silently omitted. The higher-level `live_create_terrain_stack` tool is excluded
from the one-to-one count because it composes several actions.

See `capability-matrix.md` for the remaining ArcGIS Pro surface and its planned
safety level and implementation priority.
