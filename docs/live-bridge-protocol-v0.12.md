# ArcGIS Codex live bridge protocol v0.12

Protocol v0.12 exposes 140 direct ArcGIS Pro SDK actions through the local MCP
bridge. It controls the visible project through official in-process APIs and
does not use mouse or keyboard automation.

## New typed capabilities

### Native extent indicators

- List extent indicators on any layout map frame and resolve the linked source
  frame.
- Add a native indicator between two map frames using frame, rectangle, or map
  series index-feature modes.
- Configure visibility, source frame, exterior symbolization, collapse size,
  label conflict avoidance, leader mode, frame/fill, point and leader symbols.
- Remove selected indicators only after explicit confirmation.

These are real ArcGIS Pro extent indicators. They follow the source map frame's
camera and remain editable in the layout contents pane.

### Typed map clipping

- Configure the native `CIMMap` clipping modes: none, map extent, custom shape,
  or map series.
- Bind custom clipping to a polygon layer by its ArcGIS layer URI.
- Exclude selected layers from clipping, configure full-extent behavior and
  show or hide a styled clipping-area border.

Map clipping changes the map definition only. It does not clip, overwrite, or
otherwise alter source datasets.

### Native dynamic layout text

- Add an editable ArcGIS Pro text element containing native `<dyn .../>` tags.
- Static text and dynamic tags can be mixed in the same element.
- Position, rotation, visibility, locking and text symbols are configurable.
- Existing `set_layout_text` and generic element tools can subsequently update
  or arrange the element.

## Safety and contracts

- 140 MCP tools, advertised add-in capabilities and dispatch cases are checked
  as one contract.
- MCP, protocol, DAML, project and built package versions must match.
- Source editing, destructive actions, schema changes and external publishing
  remain separately gated and disabled by default.
- Extent indicators, clipping and dynamic text change project state only;
  source datasets are not edited.

## Validation

`scripts/live_v012_validation.py` creates two temporary maps and one temporary
layout from synthetic test data. It validates custom map clipping, a two-frame
extent indicator and native dynamic date text, then disables/removes the tested
objects and deletes all temporary project items without saving the project.

## Remaining high-value areas

The next targets include typed chart builders, QR elements, richer map-series
configuration, related/subreports, annotation conversion, MGRS presets, Task
Assistant, Workflow Manager, topology/network/3D APIs and portal-aware sharing
contracts.
