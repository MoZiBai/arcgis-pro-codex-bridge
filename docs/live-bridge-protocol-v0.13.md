# ArcGIS Codex live bridge protocol v0.13

Protocol v0.13 exposes 146 direct ArcGIS Pro SDK actions through the local MCP
bridge. It controls the visible project through official in-process APIs and
does not use mouse or keyboard automation.

## New typed capabilities

### Complete map-series lifecycle

- List map-series state for one or every layout, including page count,
  navigation state and spatial-series fields.
- Configure enablement, frame, starting/current page and spatial index, name,
  number, sort, scale, rotation, spatial-reference and category fields.
- Configure best-fit/data-driven extent behavior, margin type/units and scale
  rounding.
- Export all/current/selected/custom pages to one PDF or multiple named or
  numbered files.
- Remove a map-series definition only after explicit confirmation.

### Layout alignment and distribution

- Align editable elements to the left, horizontal center, right, bottom,
  vertical center or top of a selected reference element.
- Distribute three or more elements horizontally or vertically using their
  actual page geometry.
- Match width, height or complete size to a reference element.

### Active map-view export

- Export the visible active map pane directly to PDF, PNG, JPEG, TIFF, SVG or
  SVGZ.
- Configure resolution, dimensions, transparency, world files, GeoTIFF tags,
  font embedding and selection symbology where supported.
- Output remains restricted to the configured output root.

## Safety and contracts

- 146 MCP tools, advertised add-in capabilities and dispatch cases are checked
  as one contract.
- MCP, protocol, DAML, project and built package versions must match.
- Map-series and layout arrangement operations change project state only.
- Map-view and map-series export create derived files only under the output
  root. Source datasets remain unchanged.
- Editing, destructive tools, schema changes and external publishing remain
  independently gated and disabled by default.

## Validation

`scripts/live_v013_validation.py` also covers the v0.12 clipping, extent
indicator and dynamic-text additions. It creates temporary maps and a layout
from synthetic data, validates arrangement and a spatial map series, exports a
current series page and active map view, then removes all temporary project
objects without saving the project.
