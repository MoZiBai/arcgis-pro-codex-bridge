# ArcGIS Pro live bridge design

The Python MCP server in this repository works out of process. It can inspect data, run ArcPy geoprocessing, create project copies, add layers, and export layouts. That is the right first layer for reliable automation.

It cannot attach an external Python process to the already open ArcGIS Pro application. `arcpy.mp.ArcGISProject("CURRENT")` is only available to Python running inside ArcGIS Pro.

For direct control of the visible, currently open project, add a second component built with the ArcGIS Pro SDK for .NET:

1. An ArcGIS Pro add-in starts a localhost-only named-pipe or HTTP listener.
2. The add-in exposes a small command set such as `get_current_project`, `list_maps`, `add_layer`, `set_definition_query`, `apply_symbology`, `zoom_to_layer`, `create_layout`, `export_layout`, and `save_project_copy`.
3. Commands that touch ArcGIS Pro state run through `QueuedTask.Run`.
4. The MCP server adds matching `live_*` tools and forwards requests to the add-in.
5. Every write command includes a project/session id, an output path, and an idempotency key. Source datasets remain read-only by default.

The first add-in implementation is under `addin/ArcGISCodexBridgeAddin`. It
auto-loads with ArcGIS Pro and consumes the same local file queue already used
by the MCP server. The initial vertical slice supports project status and
loading datasets into the visible map. Geoprocessing continues through the
tested ArcPy worker and its output layers are then loaded by the add-in.
