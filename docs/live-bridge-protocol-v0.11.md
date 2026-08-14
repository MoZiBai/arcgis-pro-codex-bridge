# ArcGIS Codex live bridge protocol v0.11

Protocol v0.11 exposes 134 direct ArcGIS Pro SDK actions through the local MCP
bridge. It controls the visible project through official in-process APIs and
does not use mouse or keyboard automation.

## New typed capabilities

### Dynamic layout frames

- List native table frames and chart frames with their map-member bindings.
- Add a native `TableFrame` linked to a feature layer or standalone table,
  selecting fields, row limits, columns, filling/fitting strategy and an
  optional ArcGIS style item.
- Add a native `ChartFrame` linked to an existing layer chart.
- These remain ordinary editable ArcGIS Pro layout elements and update from
  their bound map member.

### Multiple label classes

- List every label class on a feature layer.
- Create, rename and configure individual classes independently.
- Configure expression engine, filters, visibility, scales, priority, coded
  values and text symbols.
- Apply Standard or Maplex label-placement style items and selected typed
  placement properties.
- Remove selected label-class definitions only after explicit confirmation.

### Report authoring

- Create reports from a feature layer or standalone table.
- Select fields, definition query, selection use, grouping fields, statistics,
  built-in template, styling and page size.
- Configure report fields, queries, groups, page size and section properties.
- Report section removal requires explicit confirmation.

### Native grid configuration

- Configure graticule or measured-grid X/Y intervals without replacing the
  entire grid CIM.
- Toggle internal grid-line drawing while retaining the native grid object.
- Configure exterior/interior tick visibility, length and offset.
- Suppress endpoint labels independently when a border-only coordinate frame
  is required.

## Safety and contracts

- 134 MCP tools, advertised add-in capabilities and dispatch cases are checked
  as one contract.
- MCP, protocol, DAML, project and built package versions must match.
- Source editing, destructive actions, schema changes and external publishing
  remain separately gated and disabled by default.
- Layout frames, report definitions, labels and grids change project state
  only; source datasets are not edited.
- Read paths remain restricted to allowed roots and derived files remain
  restricted to the configured output root.

## Validation

`scripts/live_v011_validation.py` creates only temporary maps, layouts, label
classes, dynamic frames, grids and reports from the existing synthetic test
geodatabase. It removes all temporary project objects afterward and never saves
the project.

## Remaining high-value areas

The next targets remain typed chart builders, dynamic text and QR elements,
extent indicators and map-frame clipping, richer report related/subreport
authoring, annotation conversion, MGRS presets, Task Assistant, Workflow
Manager, topology/network/3D APIs and portal-aware sharing contracts.
