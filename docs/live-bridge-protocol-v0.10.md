# ArcGIS Codex live bridge protocol v0.10

Protocol v0.10 exposes 125 direct ArcGIS Pro SDK actions through the local MCP
bridge. It controls the visible project through official in-process APIs and
does not use mouse or keyboard automation.

## Safety and contracts

- 125 MCP tools, advertised add-in capabilities and dispatch cases are checked
  as one contract.
- MCP, protocol, DAML, project and built package versions must match.
- Source editing, destructive actions, schema changes and external publishing
  remain separately gated and disabled by default.
- Data-connection changes affect project layer/table references only. They do
  not move, rename, overwrite or delete source datasets.
- Source datasets are not modified by map, layout, style, catalog,
  presentation, query or selection operations.
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

### Data connections and source repair

- Read a feature layer, raster layer or standalone table data connection
- Replace one compatible data connection from validated CIM JSON
- Replace compatible layer/table sources from one workspace to another
- Report affected map members without altering either source workspace

### Spatial query and selection

- Query features using an inline envelope or polygon
- Build a spatial filter from the bounded union of a reference feature layer
- Select features by spatial relationship and selection combination method
- Enforce row, field, source-feature and response-size bounds

### Symbology and geoprocessing

- Core vector and raster renderers
- Editable graphics and non-destructive terrain stacks
- Generic validated geoprocessing
- Separately gated schema and publishing adapters

### Layouts, native grids and map surrounds

- Layout lifecycle, multiple map frames, map series and full layout CIM
- Editable text, pictures and graphics
- External ticks and renderer-derived color bars
- Native graticules, measured grids and reference grids from styles
- Independent legend, north-arrow and scale-bar lifecycle and CIM access
- PDF, PNG, JPEG, TIFF, SVG and SVGZ export

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
actions and are not included in the 125 direct-action count.

## Remaining high-value areas

The next targets are dynamic table/chart frames, report creation and section
authoring, multiple label classes and annotation, typed grid interval/label/tick
helpers, Task Assistant, Workflow Manager, knowledge graphs, and specialized
network, topology, 3D and analysis APIs.
