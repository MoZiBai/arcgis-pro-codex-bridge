# ArcGIS Pro 3.6.3 SDK audit for protocol v0.19

The installed public SDK exposes the active `MapView` effect setters,
`ElevationSurfaceLayer` configuration methods, layer elevation definitions,
surface sampling/profile methods and animation playback/export interfaces used
by this protocol.

Public concrete CIM effect families found in ArcGIS Pro 3.6.3 include camera
effects (`CIMDepthOfFieldEffect`, `CIMTiltShiftEffect`), visual effects
(`CIMBlackAndWhiteEffect`, `CIMBlueprintEffect`, mosaic, outline, pencil, toon,
watercolor and related types), and post-processing effects (bloom, vignette,
grain and color grading). Generic typed-CIM parsing remains available so future
effect classes do not require another Add-in action.

The public animation surface provides track/keyframe inspection, playback,
duration scaling and export. Track/keyframe creation helpers are not exposed as
equivalent public factories in the inspected assemblies; full animation-track
authoring therefore remains available through the existing protected map-CIM
round trip rather than being advertised as a typed factory.

## Next audit candidates

- voxel, LAS and exploratory-analysis inspection;
- network, topology and trace-network public APIs;
- related reports, subreports and supplemental pages;
- floor-aware maps, ranges, time sliders and geotriggers;
- knowledge graphs, tasks and workflow surfaces;
- gated portal sharing and service authoring.
