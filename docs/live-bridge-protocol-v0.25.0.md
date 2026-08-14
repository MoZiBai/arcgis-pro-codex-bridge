# ArcGIS Codex live bridge protocol v0.25.0

Protocol v0.25.0 keeps the 221 direct ArcGIS Pro SDK actions from v0.24.0 and
expands their reliability and discovery surface.

## Changes

- `set_camera` now converts every camera number to a finite JSON value; undefined
  values on an empty map are returned as `null` instead of failing serialization.
- All six `transform_view_coordinates` operations now execute on ArcGIS Pro's
  Main CIM Thread (MCT), while popup operations remain on the UI dispatcher.
- `get_layer_schema` and `get_table_schema` now return read-only advanced
  geodatabase metadata: joins/relates, subtypes, indexes, attribute rules,
  contingencies, controller datasets, archive/attachment/editor-tracking state,
  validation status fields, and bounded inspection errors.
- MCP adds `search_arcgis_sdk_registry`, a read-only search across installed public
  SDK types and member signatures. It does not execute arbitrary reflected APIs.

## Safety

- Schema inventory reports `source_data_modified=false`.
- SDK discovery reports both `source_data_modified=false` and
  `project_modified=false`.
- No project save is performed by the validation workflow.
- Runtime validation creates only temporary project objects and removes them before
  comparing the APRX and synthetic fixture fingerprints.
