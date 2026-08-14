# ArcGIS Pro 3.6.3 SDK coverage audit after v0.27

The installed ArcGIS Pro 3.6.3 assemblies remain the authoritative API surface.
Namespaces containing `.Internal.` are excluded from typed bridge candidates.

## Inventory baseline

- 16 inspected ArcGIS assemblies.
- 4,482 non-internal public types.
- 5,952 Framework command/tool/pane IDs.
- 1,977 installed geoprocessing tools.
- 236 direct typed live actions in protocol v0.27.
- 39 bounded in-memory operations within `geometry_engine`.
- The refreshed name-based entry-point sample contains 213 factory/manager/
  definition candidates; 82 are now referenced directly by bridge source and 131
  remain triage candidates. This count is not a completeness metric because many
  remaining entries are interfaces, base definitions, duplicate factories, or
  surfaces already covered by registries, CIM, or generic geoprocessing.

## v0.27 closed P1 gaps

- Active-scene exploratory analysis lifecycle for `Viewshed`, `ViewDome`, and
  `LineOfSight`.
- Native TIN, terrain, and point-cloud renderer adapters, including eye-dome
  lighting.
- Generic report element factory for seven native report element families.
- Generic presentation element factory for text, picture, shape, arrow, and
  grouping operations.
- Read-only layout printer preferences and active-view print readiness.
- Supplemental report pages/subreports/expressions, presentation transitions and
  speaker notes, plus independently gated attribute-rule evaluation.

## Broad long-tail coverage

The bridge deliberately does not create one wrapper per SDK type. Long-tail
coverage is supplied by the searchable public SDK registry, 5,952-item Framework
registry with exact allowlists, 1,977-tool geoprocessing registry with independent
safety gates, protected same-type CIM patching, in-memory GeometryEngine, and
ordered batch execution.

## Important remaining typed candidates

1. Typed DDL only behind schema and destructive gates.
2. Related-report creation and deeper related-report data-source authoring.
3. Presenter controls.
4. Utility/network trace authoring and Network Analyst solving with derived-output
   restrictions.
5. Printer execution only behind an independent external-state gate; system-wide
   installed-printer enumeration is not exposed by the public ArcGIS SDK without
   adding a platform printing dependency.

The refreshed audit also identified lower-priority candidates such as
`PresentationPageFactory`, catalog-specific project item subclasses, knowledge
graph investigation creation, LAS renderer definition variants, Network Analyst
solver definitions, and `SchemaBuilder`. These remain queued for side-effect,
licensing, overlap, and test-data review rather than being exposed merely because
their names are public.

## Deliberate non-goals

- Internal namespaces or unrestricted reflection execution.
- Arbitrary Framework command execution without exact allowlists.
- Automatic source editing, schema mutation, deletion, publishing, credentials,
  printing, or workflow transitions without separate disabled-by-default gates.
