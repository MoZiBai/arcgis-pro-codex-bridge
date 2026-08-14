# ArcGIS Pro 3.6.3 SDK coverage audit after v0.26

This audit uses the locally installed ArcGIS Pro 3.6.3 assemblies as the
authoritative surface. Namespaces containing `.Internal.` are excluded from
typed bridge candidates.

## Inventory baseline

- 16 inspected ArcGIS assemblies.
- 4,482 non-internal public types.
- 5,952 installed Framework command/tool/pane IDs.
- 1,977 installed geoprocessing tools.
- 228 direct typed live actions in protocol v0.26.
- 39 bounded in-memory operations exposed by the single `geometry_engine` action.

The raw public-type count is not a target for one-wrapper-per-type. Most types are
CIM records, enums, option objects, result objects, renderer definitions, or
interfaces consumed through a smaller number of stable factories and managers.

## Broad surfaces already covering the long tail

| Surface | Coverage role |
|---|---|
| SDK registry | Finds installed public types, constructors, methods, properties, enums and static members without arbitrary execution. |
| Framework registry | Finds exact UI IDs with localized captions and risk hints; execution remains allowlisted. |
| Geoprocessing registry | Finds 1,977 tools with localized parameters and independent destructive/schema/publishing gates. |
| Protected CIM patching | Covers display/configuration properties while guarding structural and data-connection paths. |
| GeometryEngine | Covers 39 pure in-memory geometry operations without reading or writing datasets. |
| Batch execution | Composes reviewed actions without adding a dedicated orchestration wrapper for every workflow. |

## Important public candidates still not directly bridged

### P1: scene exploratory analysis

Public types exist for `LineOfSight`, `Viewshed`, `ViewDome`, and their CIM
definitions. `MapView` exposes `GetExploratoryAnalysisCollection`,
`AddExploratoryAnalysisAsync`, and `RemoveExploratoryAnalysisAsync`. A future
generic exploratory-analysis action should support bounded inspect/add/configure/
remove operations for active scene views, with temporary-object validation.

### P1: TIN, terrain and point-cloud drawing definitions

The public Mapping SDK exposes typed definitions for TIN faces, nodes, contours,
breaklines, class breaks, terrain dirty areas/points, and point-cloud renderers.
These should extend the existing symbology adapter rather than become many thin
tools.

### P1: report and presentation element factories

`ReportElementFactory` exposes attachment frames, chart frames, dynamic pictures,
field values/statistics, paragraph text and map frames. `PresentationElementFactory`
exposes graphic, picture, text, predefined-shape, arrow and group creation. These
fit two generic element-authoring actions with bounded geometry and explicit
container resolution.

### P2: printer services

Public `PrinterSettingsInfo`, map/layout `Print` methods and CIM printer preferences
exist. Printing changes external state and should remain independently gated; a
read-only printer/settings inspection action can precede any print action.

### P2: typed geodatabase DDL and attribute-rule evaluation

`SchemaBuilder` covers feature classes, tables, feature datasets, relationship
classes, domains, indexes, rename/modify/delete and geodatabase creation. The
surface is powerful but source/schema destructive. Keep the existing generic GP
schema adapter as the default and add typed DDL only behind the schema and
destructive gates with output-root restrictions. `AttributeRuleManager.Evaluate`
is a separate candidate for a bounded validation-only action.

### P3: portal/locator configuration and credentialed connections

Public APIs can add/edit/remove portals, activate/deactivate/add/move/remove
locators, and create AGS connection files. v0.26 deliberately exposes only
read-only portal/locator inspection and non-display geocoding. Configuration must
use explicit external-state gates and must never return credentials or tokens.

## Deliberate non-goals

- Types under namespaces containing `.Internal.`.
- Unrestricted reflection invocation.
- Generic execution of arbitrary Framework commands without exact allowlists.
- Automatic source edits, schema mutation, deletion, publishing, credential use,
  printing or workflow transitions without their independent gates.

## Next implementation batch

1. Generic exploratory-analysis lifecycle for scene views.
2. Unified TIN/terrain/point-cloud renderer adapter.
3. Generic report and presentation element factories.
4. Read-only printer inventory and settings validation.
5. Attribute-rule evaluation and typed DDL design behind existing gates.
