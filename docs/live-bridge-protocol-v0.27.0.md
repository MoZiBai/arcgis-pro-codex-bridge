# ArcGIS Codex live bridge protocol v0.27.0

Protocol v0.27.0 increases direct ArcGIS Pro SDK actions from 228 to 236. It
concentrates the remaining high-value public 3.6.3 SDK factories and specialized
drawing surfaces into reusable actions so future workflows do not require a new
one-off bridge build.

## New actions

- `manage_exploratory_analysis`: inspect/add/remove/clear active-scene
  `Viewshed`, `ViewDome`, and `LineOfSight` objects with finite cameras, bounded
  targets, and explicit confirmation for removal.
- `manage_report_elements`: native `ReportElementFactory` adapter for paragraph
  text, field values/statistics, dynamic pictures, attachment frames, chart
  frames, and map frames in report sections.
- `manage_presentation_elements`: native `PresentationElementFactory` adapter
  for text, pictures, predefined shapes, arrows, grouping, ungrouping, and
  confirmed removal on presentation pages.
- `configure_specialized_renderer`: typed TIN face/edge/node/contour/elevation/
  aspect/breakline, terrain point/dirty-area, and point-cloud stretch/class-breaks/
  unique/RGB renderer authoring, including eye-dome lighting.
- `inspect_printers`: read-only layout printer preferences and active-map-view
  print readiness. It never sends a print job.
- `manage_report_composition`: supplemental layout pages, subreports, report
  expressions, layout-page replacement, and confirmed composition removal.
- `configure_presentation_transition`: typed None/Swipe/Fade/Fly transitions,
  swipe direction, duration, and speaker notes for bounded page lists.
- `evaluate_attribute_rules`: typed geodatabase evaluation, explicitly marked as
  source-modifying and disabled behind the source-editing gate.

## Safety

- Every new action reports `source_data_modified=false` and `project_saved=false`.
- Exploration removal, report/presentation element removal, and surface-renderer
  removal require explicit confirmation.
- Camera coordinates, layout/report bounds, class counts, point-cloud fields,
  group sizes, response counts, and optional CIM JSON are bounded.
- The printer action does not call `Print`, open a dialog, or change external state.
- Attribute-rule evaluation is never part of non-destructive validation; it
  requires both the disabled-by-default source-editing gate and
  `confirm_evaluate=true` because validation error tables can change.
- Report and presentation picture paths remain constrained by the MCP read roots.

## Validation

- `protocol_contract_test.py` verifies one MCP tool per direct add-in action.
- Dedicated contracts cover exploratory analysis, document element factories,
  specialized rendering, and printer non-execution.
- `live_v027_validation.py` creates only temporary scene/report/presentation
  objects, removes them before completion, and verifies the APRX file and
  synthetic source fingerprints remain unchanged.
