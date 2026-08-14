# ArcGIS Pro 3.6.3 SDK audit for protocol v0.17

This audit records the next bridge candidates after the typed vector and raster
renderer expansions. The installed ArcGIS Pro assemblies are the authority.

## Visual variables

Public CIM types are available for:

- `CIMColorVisualVariable`;
- `CIMSizeVisualVariable`;
- `CIMRotationVisualVariable`;
- `CIMTransparencyVisualVariable`.

Public renderer types exposing a `VisualVariables` array include simple,
unique-value, class-breaks, proportional and dot-density renderers. A bounded
`configure_visual_variables` action can therefore inspect, replace, append,
remove or clear visual variables without replacing the primary renderer.

The contract should support explicit field/Arcade expressions, normalization,
value and output stops, size bounds, color ramps, rotation reference system,
polygon color target and legend authoring metadata. It must reject renderer and
geometry combinations that ArcGIS does not support and return the actual CIM
visual-variable classes after applying them.

## Dictionary and bivariate rendering

ArcGIS Core exposes `CIMDictionaryRenderer`, bivariate authoring information and
bivariate field/grid objects, but ArcGIS Desktop Mapping 3.6.3 exposes no public
`DictionaryRendererDefinition` or `BivariateChoroplethRendererDefinition`.
The Pro UI implementation is internal. These renderer families must therefore
remain a separately tested CIM path rather than being advertised as typed SDK
support.

Dictionary rendering additionally depends on an installed dictionary style and
correct field mappings. A future action must list compatible dictionary styles
and validate every mapped field before setting the renderer.

## Raster function chains

`CIMRasterLayer.RenderingRule` accepts a public `CIMRenderingRule` with name,
definition, variable name and argument dictionary. The Raster Function gallery,
editor and function discovery APIs found in 3.6.3 are internal UI types.

A safe bridge should therefore expose:

1. read-only inspection of the current rendering rule;
2. an allowlisted set of well-known rendering functions with typed arguments;
3. import of a validated raster-function template from an allowed data root;
4. explicit removal/restoration of the project-only rendering rule.

An unrestricted arbitrary argument dictionary should not be the default MCP
surface because function names and argument types vary by extension and data
source.

## Multidimensional raster display

Public CIM types include `CIMRasterMultidimensionalDisplayDefinition` and
`CIMRasterMultidimensionalExtentDefinition`. The display definition covers
variable name, time value, range dimension and additional dimension values.
Rich metadata discovery and the Pro slice UI are internal.

The implementation should first add read-only inventory of variables and
dimensions through dataset metadata/ArcPy, then a bounded action for setting an
active slice only after validating the requested variable and dimension values.

## Proposed implementation order

1. Configure and inspect visual variables on supported feature renderers.
2. Inspect/set/remove allowlisted raster rendering rules.
3. Inventory and configure multidimensional active slices.
4. Add dictionary and bivariate CIM paths only after native sample renderers can
   be captured and round-tripped in temporary maps without saving the project.
