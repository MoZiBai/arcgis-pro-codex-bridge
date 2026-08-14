# ArcGIS Pro 3.6.3 SDK audit for protocol v0.18

The installed 3.6.3 assemblies expose public `GetDefinition` and
`SetDefinition` methods for `Element`, `StandaloneTable`, `Report`,
`Presentation`, and `PresentationPage`. `ElementFactory.CreateElement` also
accepts an arbitrary concrete `CIMElement` definition. These are stable public
SDK surfaces and therefore suitable for a generic bridge layer.

## Structural safety rules

Same-type parsing is enforced for every replacement. By default the bridge
preserves the properties that bind an authoring object to project structure or
source data:

- element names, element/map-frame URIs, map-frame links, map-member URIs,
  chart names and element-storage URIs;
- standalone-table source connection, source/portal URIs and selection URI;
- report identity, section collection and map-series definition;
- presentation identity and page membership;
- presentation-page element storage, thumbnail, map view and image/video source
  properties.

Callers may explicitly opt out of selected preservation rules only on an action
that already requires replacement confirmation. No action edits dataset rows,
schema or source files.

Runtime validation found that ArcGIS Pro 3.6.3 hangs inside complete
`Report.SetDefinition(CIMReport)` even when the unchanged definition is supplied.
The bridge therefore applies safe top-level report properties through typed
methods and uses per-element CIM setters for report sections. This limitation is
an SDK runtime behavior, not an MCP timeout workaround.

## Remaining high-value public SDK families

The next audit batch should focus on:

1. active map-view visual/camera/post-processing effects and scene drawing;
2. elevation surfaces, 3D layer elevation and vertical exaggeration;
3. animation tracks/keyframes and animation export;
4. typed chart construction on top of the existing chart-CIM endpoint;
5. related reports, subreports and supplemental report pages;
6. topology, network, voxel/LAS and exploratory-analysis inspection surfaces;
7. portal-aware sharing plans that remain gated and disabled by default.
