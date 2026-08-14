# ArcGIS Codex live bridge protocol v0.9

Protocol v0.9 exposes 120 direct ArcGIS Pro SDK actions through the local MCP
bridge. It controls the visible project through official in-process APIs and
does not use mouse or keyboard automation.

## Safety and contracts

- 120 MCP tools, advertised add-in capabilities and dispatch cases are checked
  as one contract.
- MCP, protocol, DAML, project and built package versions must match.
- Source editing, destructive actions, schema changes and external publishing
  remain separately gated and disabled by default.
- Source datasets are not modified by map, layout, style, catalog or
  presentation operations.
- Read paths are restricted to allowed roots; outputs are restricted to the
  configured output root.

## Capability families

### Project, maps and map members

- Project inventory, metadata, save and Save As
- Map lifecycle, camera, bookmarks and full map CIM
- Layer and standalone-table lifecycle
- Bounded schema/query/statistics/selection
- Labels, popups, fields, display filters, time and chart CIM
- Confirmed and disabled-by-default source editing

### Symbology and geoprocessing

- Core vector and raster renderers
- Editable graphics and non-destructive terrain stacks
- Generic validated geoprocessing
- Separately gated schema and publishing adapters

### Layouts and exports

- Layout lifecycle, multiple map frames, map series and full layout CIM
- Editable text, pictures and graphics
- External ticks and renderer-derived color bars
- PDF, PNG, JPEG, TIFF, SVG and SVGZ export

### Native map grids

- Search referenced styles for grid items
- Add native ArcGIS graticules, measured grids and reference grids from styles
- List native grids with optional CIM JSON
- Remove selected grids with confirmation
- Replace graticule or measured-grid CIM definitions with confirmation

### Map surrounds

- Search styles for legend, north-arrow and scale-bar items
- Independently add, list, move, resize, relink, show/hide, lock and restyle map
  surrounds
- Configure core legend and scale-bar properties
- Replace individual legend, north-arrow or scale-bar CIM definitions with
  confirmation

### Reports, presentations and catalog

- Existing-report lifecycle and PDF export
- Presentation lifecycle, blank/map/image/video pages, export and PRSX save
- Catalog path items, styles, broken-reference repair and favorites

### Attachments and relationships

- Attachment metadata and safe export
- Gated attachment add/update/remove
- Relationship-class inventory and bounded related-record traversal

## Composite tools

The terrain-stack, schema-tool and publishing-tool helpers compose direct
actions and are not included in the 120 direct-action count.

## Remaining high-value areas

The next targets are typed grid interval and label configuration, legend item
depth, dynamic table/chart frames, report creation, typed data-source
replacement, spatial relationship queries, Task Assistant, Workflow Manager,
knowledge graphs and specialized analysis/network APIs.
