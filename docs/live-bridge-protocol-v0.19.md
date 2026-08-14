# ArcGIS Codex live bridge protocol v0.19

Protocol v0.19 adds 10 direct scene, active-view, elevation and animation
actions, raising the direct SDK action count from 174 to 184. It also extends
map creation so the bridge can create 2D maps, local scenes and global scenes
with an explicit ArcGIS basemap choice.

## Active map-view and effects

- Inspect active 2D/3D viewing mode, scene drawing mode, field of view, camera
  effect, visual effect and post-processing effect stack.
- Configure those properties with either complete concrete CIM effect JSON or
  typed presets.
- Typed visual presets cover black-and-white, blueprint, mosaic, pixelation,
  halftone, outline, pencil, toon, watercolor and monochromatic effects.
- Typed camera presets cover depth of field and tilt shift; post-processing
  presets cover bloom, vignette, grain and color grading.

## Elevation and 3D display

- List ground and custom elevation surfaces, optionally with full CIM.
- Configure surface visibility, transparency, vertical exaggeration, shading,
  TIN shading, navigation and display color.
- Inspect or configure each layer's elevation type, custom surface, vertical
  exaggeration, cartographic offset, height field and expression.
- Sample bounded point elevations and elevation profiles without modifying any
  source geometry or raster.

## Animation

- Inspect animation names, tracks, keyframe times, duration and export state.
- Play, stop, step keyframes, scale duration and stop an export.
- Begin MP4, AVI or GIF export under the configured output root.

## MCP-side generic CIM merge patches

`live_patch_cim_object` is a high-level MCP composition tool rather than a new
Add-in action. It reads an object's existing CIM JSON, applies an RFC 7396-style
merge patch, and sends the resulting complete definition through the existing
same-type CIM setter. Supported targets are maps, layers, layouts, layout
elements, standalone tables, reports, report elements, presentations,
presentation pages and presentation-page elements.

- `dry_run=true` returns the changed JSON paths without changing project state.
- `expected_cim_type` detects a target or subtype change between inspection and
  application.
- Applying a patch requires `confirm_apply=true`.
- URI, data-connection, member, page, element and Map Series structure paths are
  rejected by default. They require the separate
  `allow_structural_changes=true` opt-in as well as application confirmation.
- Patches are limited to 1 MB and changed-path output is bounded.
- A complete report definition is never replaced on ArcGIS Pro 3.6.3. Report
  patches can only reach properties supported by the safe typed report setter;
  use report-element patches for section contents.

This layer is intended for long-tail display and authoring properties so adding
a new CIM property usually does not require rebuilding or reloading the Add-in.

All actions operate on in-memory project/view state. They do not save the APRX,
edit rows, alter schema, or overwrite source data.
