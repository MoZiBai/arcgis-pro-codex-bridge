# ArcGIS Pro 3.6.3 SDK audit for protocol v0.20

This audit used reflection over the locally installed ArcGIS Pro 3.6.3 public
assemblies rather than relying on UI command names or an older online SDK
version.

## Confirmed public surfaces

`ArcGIS.Desktop.Mapping.MapView` publicly exposes read/write `Time`, `Range` and
`FloorFilter` properties, range-name inventory and asynchronous active-range
selection. The corresponding public types are `TimeRange`, `RangeExtent`,
`TimeReference` and `CIMFloorFilterSettings`.

`GraphicsLayer` implements the public `IElementContainer` contract for element
inventory, lookup, movement, selection, removal and clearing. Its elements
implement `IElement`, including transform, geometry, state and complete CIM
round trips. This supports a generic graphics management surface rather than a
separate action for every graphic subtype.

`LasDatasetLayer` publicly exposes eye-dome lighting, display filters,
selectable classes, visible-point selection, active constraints and bounded
selection operations. `LasDataset` exposes dataset-level file, size, extent,
class and return inventory.

`VoxelLayer` publicly exposes variable profiles, volumes, time values, lighting,
vertical display, visualization mode, sections/slices/locked sections and
isosurface containers. `VoxelVariableProfile` exposes renderer CIM and
isosurface lifecycle. Source voxel values are not edited by these APIs.

## Deferred or gated from this batch

- LAS point-selection geometries and class-code edits need separate bounded and
  source-edit safety contracts.
- Voxel section/slice creation and deletion need coordinate-space validation and
  explicit project-object deletion confirmation.
- Topology, utility network, trace network and Network Analyst expose public
  layer/data handles, but useful operations depend on extension-specific Core
  data contracts that require a separate safety and lifetime audit.
- Knowledge Graph, Task Assistant and Workflow Client are separate installed
  assemblies and remain next-stage audit targets.
