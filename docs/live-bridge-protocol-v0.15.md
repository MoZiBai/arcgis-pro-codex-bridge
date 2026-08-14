# ArcGIS Codex live bridge protocol v0.15

Protocol v0.15 keeps the 154 direct ArcGIS Pro SDK actions and substantially
widens the native `apply_vector_symbology` contract. The bridge controls the
visible project through official in-process APIs and does not use mouse or
keyboard automation.

## Expanded native vector renderers

The parameterized vector-symbology action now creates and validates these
public ArcGIS Pro 3.6 renderer definitions:

- simple and unique value;
- graduated colors and graduated symbols;
- proportional symbols;
- unclassed colors;
- point heat maps;
- polygon dot density;
- pie, bar and stacked chart renderers.

Every request is checked with `FeatureLayer.CanCreateRenderer` before the
renderer is created. Heat maps are restricted to point/multipoint layers and
dot density to polygon layers. The contract includes classification,
normalization, symbol-size ranges, null handling, exclusions, heat radius and
quality, dot size/value, chart fields, chart dimensions, orientation and color
ramps.

ArcGIS Pro 3.6.3 exposes bivariate renderer CIM objects but no public typed
`BivariateChoroplethRendererDefinition`. Bivariate authoring therefore remains
unadvertised until a CIM implementation has dedicated runtime validation.

## Safety and validation

- Renderer changes mutate only temporary project/map state; source rows and
  schemas are untouched.
- Source editing, destructive operations, schema changes and external
  publishing remain independently gated and disabled by default.
- `scripts/create_vector_validation_data.py` creates an isolated point/polygon
  geodatabase under the output root. `scripts/live_v015_validation.py` creates
  one temporary map from it, applies and inspects all eight newly exposed
  renderer families, removes the map and verifies both the open `.aprx`
  metadata and every generated vector-input file hash are unchanged.
- `scripts/live_validation_suite.py` combines the compatible v0.13, v0.14 and
  v0.15 checks and confirms no temporary validation objects remain.
