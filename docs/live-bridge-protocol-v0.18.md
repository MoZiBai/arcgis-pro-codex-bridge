# ArcGIS Codex live bridge protocol v0.18

Protocol v0.18 adds 15 direct generic CIM actions, raising the direct ArcGIS Pro
SDK action count from 159 to 174. The purpose of this batch is restart
reduction: long-tail display and authoring properties can be transformed on the
MCP side and applied through stable object-level round-trip endpoints instead
of adding a new Add-in action for every property.

## New object-level CIM coverage

- Layout elements: inspect, replace a same-type definition, or create a new
  concrete editable element from CIM JSON.
- Standalone tables: inspect and replace their definition while preserving URI,
  source URI, portal URI, data connection and selection storage by default.
- Reports: inspect complete report definitions, safely apply supported top-level
  definition fields, and inspect/replace individual report sections/elements.
  ArcGIS Pro 3.6.3's complete `Report.SetDefinition` is intentionally disabled
  because a same-definition runtime round trip hangs the SDK queue.
- Presentations: inspect and replace presentation definitions, individual page
  definitions, and page-element definitions. Page membership and map/media
  storage links are preserved by default.

All replacement actions require `confirm_replace_definition=true`, reject CIM
type changes, and return ArcGIS Pro's actual applied CIM JSON. They modify only
the open project's in-memory authoring state; source rows and files are not
edited, and the project is not saved automatically.

## Restart behavior

This version is intentionally a broad foundation layer. Once loaded, most
future layout-element, table, report and presentation refinements can be
implemented as MCP-side CIM transformations without changing the Add-in DLL or
restarting ArcGIS Pro.

## Validation

`scripts/live_v018_validation.py` validates all object families using temporary
maps, layouts, reports and presentations backed only by generated validation
data. It removes the temporary project objects, never saves the project, and
verifies that the APRX file and synthetic input fingerprints remain unchanged.
