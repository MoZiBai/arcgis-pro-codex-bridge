# ArcGIS Codex live bridge protocol v0.23.2

Protocol v0.23.2 completes the runtime-threading and null-state hardening found
during the v0.23.1 live regression. The direct action count remains 219.

- Framework pane and dock-pane inventory treats individual SDK property failures
  as unavailable values, so one partially initialized Pro pane cannot fail the
  complete application-state response.
- `configure_active_view_context` keeps map/layer resolution and selection on
  the mapping worker thread, but awaits `SetMapTOCContentAsync` on the ArcGIS Pro
  WPF dispatcher where the SDK requires it.
- Empty maps and scenes now report an empty animation state. Stop operations are
  safe no-ops when no animation exists; play, navigation, scaling and export
  still return an explicit no-animation error.

All v0.23.1 raster, finite-number, elevation-query and policy hot-reload safety
changes remain in force. No action saves the project implicitly.
