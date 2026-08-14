# ArcGIS Codex live bridge protocol v0.14

Protocol v0.14 exposes 154 direct ArcGIS Pro SDK actions through the local MCP
bridge. It controls the visible project through official in-process APIs and
does not use mouse or keyboard automation.

Patch v0.14.1 corrects the ArcGIS 3.6 extent-indicator binding contract:
`CIMExtentIndicator.SourceMapFrame` stores the source layout map-frame name,
not the underlying map URI. The add, configure and inventory actions now use
that native reference consistently.

## New typed capabilities

### Reusable native ArcGIS files

- Save a map and its complete layer configuration as a native `.mapx` file.
- Save a layout as a reusable native `.pagx` file.
- Export and import native ArcGIS bookmark collections as `.bkmx` files.
- All written files remain restricted to the configured output root.

### Typed map and page properties

- Configure reference scale, map background/surface color, wraparound, snapping
  to a custom scale list, overprint simulation, feature caching, service layer
  IDs, default scale and default rotation.
- Configure layout page width, height, centimeters/millimeters/inches/points,
  rulers, guides, margins, ruler divisions and optional element resizing.

### Expanded bookmark lifecycle

- Rename bookmarks and configure description and folder.
- Refresh a bookmark's extent from the currently active map view.
- Reorder bookmarks by index.
- Import and export reusable bookmark collections.

## Safety and contracts

- 154 MCP tools, advertised add-in capabilities and dispatch cases are checked
  as one contract.
- MCP, protocol, DAML, project and built package versions must match.
- Map, layout and bookmark configuration changes project state only.
- MAPX, PAGX and BKMX writes create derived reusable files under the output
  root; source datasets are unchanged.
- Editing, destructive tools, schema changes and external publishing remain
  independently gated and disabled by default.

## Validation

`scripts/live_v014_validation.py` creates temporary maps and a layout from
synthetic test data, validates map scales/properties, bookmark configuration,
ordering and BKMX round-trip, page configuration and MAPX/PAGX output. It then
removes all temporary project objects without saving the project.

`scripts/live_validation_suite.py` runs the v0.12/v0.13 and v0.14 runtime
checks together. In addition to action-level assertions, it hashes every file
in the synthetic geodatabase before and after, checks that the open `.aprx`
file size and modification time are unchanged, and confirms that no temporary
validation map or layout remains in the project.
