# ArcGIS Codex live bridge protocol v0.8

Protocol v0.8 is a localhost-only file-queue RPC contract between the Codex MCP
server and an auto-loaded ArcGIS Pro 3.6 SDK add-in. It operates on the visible
project through official ArcGIS Pro SDK APIs, not mouse or keyboard automation.

## Contract and safety

- 111 direct live MCP tools map one-to-one to 111 advertised add-in actions.
- protocol_contract_test.py verifies MCP tools, advertised capabilities and the
  add-in dispatch switch together.
- version_contract_test.py verifies MCP, protocol, DAML, project and built
  add-in package versions together.
- Source editing, destructive operations, schema changes and publishing are
  separately gated and disabled in config.json by default.
- Read paths must be inside an allowed root. Derived outputs, exports, copied
  projects, attachment exports and presentation files must be under the output
  root.
- Removing project items removes references only and never deletes source
  datasets.

## Direct action families

### Project, metadata, maps and navigation

- Project inventory, explicit save and Save As
- Metadata read and controlled writes
- Map create/copy/rename/remove/open
- Camera, full extent, bookmarks and complete map CIM access

### Layers, tables, query, selection and editing

- Layer and standalone-table lifecycle
- Bounded schema, row query, statistics and selection
- Popups, fields, display filters, time, labels and charts
- Feature create/update/delete, save/discard and undo/redo, disabled by default

### Attachments and relationship classes

- List attachment metadata and export attachment bodies
- Add, update and remove attachments behind source-edit and destructive gates
- List relationship classes and perform bounded related-record traversal

### Symbology, raster, graphics and geoprocessing

- Core vector renderers and raster colorizers
- Editable map graphics and a non-destructive terrain-stack composite
- Generic validated geoprocessing plus separately gated schema/publishing tools

### Layouts, map frames, map series and export

- Full layout lifecycle, multiple map frames, map series and layout CIM access
- Text, pictures, graphics, grouping, drawing order, external ticks and color bars
- PDF, PNG, JPEG, TIFF, SVG and SVGZ export

### Reports

- List, duplicate, rename and remove existing reports
- Export existing reports to PDF

### Catalog, connections, styles and favorites

- List project items, defaults and favorites
- Add supported path items such as folders, geodatabases, toolboxes, styles and
  AGS server connection files
- Remove references, repair broken references and manage favorites

### Presentations

- List/create/copy/rename/remove/open presentations
- Configure aspect ratio and blank/map/image/video pages
- Configure page visibility, locking, timing, background, map camera and media
- Export pages and save editable PRSX files

## Composite MCP workflows

These higher-level tools compose direct actions and are excluded from the
111-action one-to-one count:

- live_create_terrain_stack
- live_run_schema_tool
- live_run_publishing_tool

## Remaining high-value SDK surface

The main incomplete areas are advanced typed renderers, multi-class labeling,
annotation, native grids/graticules, individual map-surround editing, dynamic
table/chart frames, report creation, typed data-source replacement, spatial
relationship queries, Task Assistant, Workflow Manager, knowledge graphs and
specialized extension APIs. See capability-matrix.md.
