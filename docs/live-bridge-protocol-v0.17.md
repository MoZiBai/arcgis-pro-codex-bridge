# ArcGIS Codex live bridge protocol v0.17

Protocol v0.17 batches five new direct SDK/CIM actions into one Add-in update,
raising the direct action count from 154 to 159.

## New capabilities

- Configure size, color, transparency and rotation visual variables on simple,
  unique-value, class-breaks, proportional and dot-density renderers.
- Inspect, set and remove an allowlisted raster rendering rule. Arguments are
  bounded to primitive values and arrays; source pixels are never changed.
- Inspect, set and clear multidimensional active variable/time/range slices.
- Get complete layer CIM JSON.
- Replace a same-type layer CIM definition while preserving layer identity,
  source data connections, group membership and graphics storage.

The protected layer-CIM round trip is the main restart-reduction mechanism:
many future display, symbol, effect and layer properties can now be changed by
MCP-side CIM transforms without adding another in-process DLL action.

## Validation

`scripts/live_v017_validation.py` uses only generated vector and raster inputs.
It applies all four visual-variable families, verifies their CIM classes,
round-trips feature-layer CIM while checking source preservation, sets/removes a
Hillshade rendering rule, sets/clears a multidimensional slice, removes the
temporary map and verifies the project and synthetic input hashes are unchanged.
