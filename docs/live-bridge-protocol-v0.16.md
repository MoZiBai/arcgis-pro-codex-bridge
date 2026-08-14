# ArcGIS Codex live bridge protocol v0.16

Protocol v0.16 extends the existing raster-symbology action without adding
another thin bridge action. The 154-action protocol now covers every public
ArcGIS Pro 3.6 `RasterColorizerDefinition` family exposed by the Mapping SDK.

## Expanded raster colorizers

- RGB composites with band mapping, independent gamma, stretch settings,
  background handling and optional custom per-channel minima/maxima.
- Raster unique-value colorization from an attribute-table field.
- Colormap colorization from bounded inline value/color/label entries or an
  allowed-root `.clr` file.
- Two-band vector-field colorization with magnitude/direction or U/V inputs and
  the public ArcGIS symbolization modes.
- Shaded relief with traditional or multidirectional illumination, none or
  adjusted scaling, edge handling and pixel-size controls.
- Common brightness, contrast and resampling parameters on all created raster
  colorizers.

Every request still passes `BasicRasterLayer.CanCreateColorizer` before
creation. Unsupported band counts, missing raster attribute tables and
incompatible datasets are rejected by ArcGIS rather than approximated.

## Validation

`scripts/create_raster_validation_data.py` creates small RGB, categorical,
vector-field and elevation rasters entirely under the output root and computes
statistics and pyramids before testing. `scripts/live_v016_validation.py`
creates one temporary map, exercises RGB, unique value, inline/file colormap,
vector field and enhanced shaded relief, checks the actual CIM colorizer class,
removes the map and verifies that neither the open project file nor the
synthetic raster inputs changed.
