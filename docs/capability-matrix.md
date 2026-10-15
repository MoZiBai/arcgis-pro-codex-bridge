# ArcGIS Pro bridge capability matrix

Target runtime: ArcGIS Pro 3.6.3. Installed assemblies and the public Pro SDK
surface are authoritative. Functions without a usable public typed API are
routed through validated geoprocessing, ArcPy, CIM, or an explicitly gated
external service.

## Safety levels

| Level | Meaning | Default |
|---|---|---|
| R | Read-only inspection | Enabled |
| P | Reversible project/map/layout mutation; source datasets unchanged | Enabled |
| O | New derived output under the configured output root | Enabled |
| D | Source rows, attachments, fields, datasets, or project items changed/deleted | Disabled |
| X | Publish, share, upload, or external-state change | Disabled |

## Capability families

| Family | Current bridge | Remaining typed work | Safety | Priority |
|---|---|---|---|---|
| Project status and inventory | Strong: project state, maps, layouts, reports, presentations, styles, catalog items, defaults, favorites, active-view readiness/TOC mode/member selection/camera history, plus bounded Framework pane/add-in state; derived-project inventory and before/after change summaries | Packaging | R/P/O | Extend |
| Project persistence | Explicit save and Save As; transactional template-copy creation with map/layout/map-frame/base-layer initialization and rollback backup; SDK-backed `live_create_blank_project` can create an untitled `.aprx` when no project is loaded, requires an explicit destination, verifies the exact current-project path, and optionally bootstraps map/layout/frame/data | Project/package creation options and richer template catalogs | P/O | Extend |
| Map lifecycle | Create, rename, duplicate, remove and open | None essential | P | Done |
| Map properties | Camera/full extent, typed clipping, reference/default scale, custom scale lists, snapping, background/surface colors, wraparound, overprint, service IDs, active-view time/range/floor filters and complete map CIM | Geotriggers and richer slider stepping/playback | P | Extend |
| Layers and standalone tables | Add, group, duplicate, reorder, manage and remove references; inspect/replace individual data connections and replace compatible workspace sources | Richer composite/service creation, join authoring and connection validation helpers | R/P | P1 |
| Layer inspection | Source, renderer/colorizer, labels, fields, popups, filters, time, charts and complete layer CIM; read-only joins/relates, subtypes, indexes, attribute rules, contingencies, controller datasets, archive/attachment/editor-tracking state; protected same-type layer CIM round-trip preserving source and structure | Join/relate authoring and typed scale-dependent effects | R/P | Extend |
| Feature/table query | Bounded fields, rows, counts, statistics, geometry summaries and spatial relationship filters from inline geometry or a reference layer | Pagination tokens, richer aggregates and distance/geodesic helpers | R | Extend |
| In-memory geometry | Restricted GeometryEngine adapter with Esri JSON/WKT input; projection, planar/geodesic buffers and measurements, overlay, predicates/DE-9IM, simplify/generalize/densify, affine transforms, nearest point and multipart splitting | Advanced geodetic construction, linear referencing and 3D multipatch operations | R | Extend |
| Selection | Attribute/ID/spatial selection, selection inventory and clear | Switch-selection and richer selection-combination helpers | R/P | Extend |
| Camera and bookmarks | Get/set camera, full extent, fixed/selection navigation and camera history; layer/bookmark zoom; bookmark create/rename/description/folder/extent/reorder/remove; BKMX import/export; active-view PNG/JPEG capture | Time/range bookmark details and bookmark thumbnail authoring | P/O | Extend |
| Vector symbology | Simple, unique-value, graduated color/symbol, proportional, unclassed color, heat map, dot density and pie/bar/stacked chart renderers; size/color/transparency/rotation visual variables; typed parameters and renderer validation | Bivariate and dictionary authoring plus advanced symbol-layer effects | P | Extend |
| Raster and surface symbology | All public 3.6 raster colorizer definitions; common advanced parameters; allowlisted rendering rules; multidimensional slices; TIN face/edge/node/contour/elevation/aspect/breakline definitions; terrain point/dirty-area definitions; point-cloud stretch/class-breaks/unique/RGB and eye-dome lighting | CMYK through validated CIM, richer raster-function-template discovery, and more specialized surface label controls | P/O | Extend |
| Labels and annotation | Multiple native label classes; per-class expression/filter/visibility/scales/priority/text; Standard and Maplex style/placement helpers | Halo/callout authoring, annotation conversion and conflict diagnostics | P/O | Extend |
| Popups and display filters | Core fields, aliases, visibility, popup import/export and display filters | Media and expression authoring | P | Extend |
| Map graphics | Point/line/polygon/rectangle/text factories; element inventory, transforms, selection, reordering, confirmed removal and complete same-type CIM/merge patches | Typed picture/SVG factories and grouping helpers | P | Extend |
| Geoprocessing | Searchable 1,977-tool installed ArcPy catalog with localized parameter inspection, safety hints and generic validated execution/output discovery; MCP jobs now expose bounded progress, cooperative cancellation, history, and output-root artifact inventory | ArcGIS-native per-tool progress callbacks and history persistence; broader toolbox enablement after risk classification | R/O/D | Extend |
| Active view interaction | Native TOC/member selection, draw pause/redraw/cancel, bounded feature flash, feature/table popups, camera navigation, feature-cache lifecycle and map/client/screen coordinate conversion | Custom popup media and controlled printing | R/P/O/X | Extend |
| Data schema | Read-only advanced schema inventory; separately gated allowlisted schema GP adapter; typed attribute-rule evaluation behind source-editing confirmation because validation error tables may change | Typed DDL and domain/subtype/index authoring | R/O/D | P1 gated |
| Feature editing | Create/update/delete, save/discard and undo/redo | Richer geometry transformations and topology-aware editing | D | Extend gated |
| Attachments and relationships | List/export/add/update/remove attachments and bounded relationship traversal | Relationship creation and deeper relationship graph traversal | R/D | Extend gated |
| Layout lifecycle | Create/import/copy/rename/remove/open, typed page size/units/ruler settings, full native guide lifecycle, PAGX save, full layout CIM, basic bounding-box checks for overflow, overlap, element size, required surrounds, and legend population; optional export preflight; point text and external tick labels may report false zero-size errors | Reliable text bounds, intentional-overlap handling, leader-line collision checks, visual QA, automatic repair suggestions, printer preferences and typed margins | P/O | Extend |
| Layout elements | Static/native dynamic text, picture/graphics, transform, grouping/drawing order, geometry-based alignment/distribution and size matching | QR and richer dynamic elements | P | Extend |
| Map frames | Multiple frames, linked map, extent/bookmark camera, rotation, aspect, border and native extent-indicator lifecycle/configuration | Per-frame layer overrides and richer camera constraints | P | Extend |
| Map surrounds | Independently list/add/configure/relink/restyle legends, north arrows and scale bars; native legend item filtering and ordering by layer name for add/configure; plus confirmed CIM replacement; layout validation reports actual native legend item counts and empty legends | More typed legend item formatting and richer style parameters | P | Extend |
| Grids and graticules | Native style-based grids plus list/remove/CIM replacement; typed graticule/measured-grid X/Y interval, internal-line, endpoint-label and tick helpers; external ticks remain available | MGRS presets and richer label formatting | P | Extend |
| Map series | Spatial/bookmark/thematic lifecycle; page inventory/navigation; spatial index/name/number/sort/scale/rotation/category/extent/margin configuration; all/current/selected/custom page export | Thematic-specific styling and richer page selection helpers | P/O | Extend |
| Dynamic layout content | Pictures, editable static/native dynamic text, native dynamic table frames and chart frames linked to map members | Typed dynamic-token builders, QR helpers and richer field/frame formatting | P | Extend |
| Export | PDF, PNG, JPEG, TIFF, SVG, SVGZ, EPS and AIX layouts/map views; PDF/PNG/JPEG/TIFF/SVG/SVGZ presentations; map-series multi-page export; report PDF | Print and richer color-profile/security options | O/X | Extend |
| Reports | Create from map members with fields/query/selection/groups/statistics/template/styling/page; configure groups, fields and sections; native paragraph, field value/statistic, dynamic picture, attachment/chart/map frames; supplemental layout pages, subreports, expressions and page replacement; lifecycle and PDF export | Related-report creation and deeper related data-source authoring | P/O | Extend |
| Presentations | Create/copy/rename/remove/open; blank/map/image/video pages; native text/picture/predefined-shape/arrow/group factories; None/Swipe/Fade/Fly transitions, speaker notes; configure/export/save PRSX | Presenter controls | P/O | Extend |
| Charts | CIM chart list and confirmed replacement | Typed chart builders and chart-pane lifecycle | P | P2 |
| Catalog and connections | List/add/remove/repair path items, styles, defaults and favorites; MAPX/PAGX/BKMX reusable native files | Credentialed server/cloud connections and validation | R/P/O | Extend |
| Application services | Read-only portal state without secrets, IANA/Windows time-zone catalog and conversion, locator inventory, non-display geocoding, bounded operation history, and read-only layout printer preferences/active-view print readiness | Gated portal/locator configuration and actual print execution | R/P/D/X | Extend |
| Metadata | Bounded XML read and controlled project/map/layout/report writes | Validation and richer import/export options | R/P/O | P2 |
| Sharing and publishing | Separately gated allowlisted staging/package/upload GP adapter | Portal-aware typed contracts and status polling | O/X | P3 gated |
| Tasks and workflow | Task Assistant item/task inventory, open/close/import/export; Workflow connection, bounded job search/details and separately gated step control | Task step-response automation, event monitoring and task execution-state inspection | R/P/O/X | Extend |
| Knowledge graph | Investigation/data-model inventory, bounded openCypher/search value materialization, open/rename and native link-chart display/layout/root-node control | Investigation creation, graph analytics and separately gated graph edits | R/P | Extend |
| Specialized extensions | Generic GP route; typed LAS and voxel display/inspection; typed TIN/terrain/point-cloud renderer authoring; active-scene Viewshed/ViewDome/LineOfSight lifecycle; typed geodatabase-topology, utility-network, trace-network, Network Analyst and network-dataset inspection | Utility/network trace authoring, Network Analyst solving, parcel, topology validation/editing, voxel section/slice authoring and other extension APIs | R/P/O/D | Ongoing |
| SDK discovery | Read-only searchable catalog over installed public Pro SDK types, constructors, methods, properties, enums, assemblies and signatures; no unrestricted reflection execution | Persisted full index and documentation links | R | Extend |

## Implementation policy

1. Prefer typed SDK actions for visible-project operations.
2. Prefer one validated geoprocessing adapter over hundreds of thin wrappers.
3. Bound every query by rows, fields, IDs and output size.
4. Require output paths under the configured output root.
5. Keep source editing, destructive actions, schema changes and publishing as
   independent gates that are disabled by default.
6. Report whether each mutation changed project state, source data, derived
   output, or external state.
7. Change add-in capabilities, dispatch, MCP schemas/routes, protocol tests,
   version tests and documentation together.
