# ArcGIS Codex live bridge protocol v0.21

Protocol v0.21 adds 8 direct active-view and feature-interaction actions,
raising the direct SDK action count from 194 to 202. These operations use the
ArcGIS Pro SDK view model directly; they do not use mouse or keyboard control.

## Active view context and TOC state

- `get_active_view_context` reports view readiness, pause/print state, view size,
  TOC content mode, camera history, camera values and selected layers, tables,
  elevation surfaces/sources, legend classes and data-source member.
- `configure_active_view_context` pauses or resumes drawing, switches native TOC
  modes, selects map/elevation members, clears TOC selection, cancels an active
  draw and requests redraw.

## Native navigation and feature interaction

- `navigate_map_view` performs fixed zoom, next/previous camera, or selected
  feature zoom/pan with an optional native animation duration.
- `flash_features` flashes a bounded list of feature object IDs and can request
  their native popups.
- `show_feature_popup` opens a dockable or appended native popup for a feature
  or table row; `close_feature_popups` closes all popups in the active view.

## Feature cache and thumbnails

- `manage_feature_cache` inspects, fills, clears or cancels the native map
  feature cache for all or named layers and controls automatic cache filling.
- `capture_view_thumbnail` writes the active view to PNG or JPEG under the MCP
  output root with bounded pixel dimensions and JPEG quality.

Selections, popups, cache content and drawing state are in-memory UI/project
state. Source feature rows and GIS datasets are not edited, and the APRX is not
saved automatically.
