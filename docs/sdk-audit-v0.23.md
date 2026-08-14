# ArcGIS Pro 3.6.3 SDK audit for protocol v0.23

The locally installed ArcGIS Pro 3.6.3 Framework assembly and shipped DAML
registries expose a broad public command and pane automation surface that can
cover many long-tail Pro features without adding a new compiled wrapper for
every ribbon button.

## Framework application surface

`FrameworkApplication` publicly exposes application state, active tab/tool,
ribbon and Backstage state, pane/dock-pane managers, installed add-in metadata,
plug-in wrappers, command execution, tab activation and tool activation.
`Pane`, `DockPane`, and `PlugInWrapper` expose enough public state for bounded
inventory and exact-ID control.

The local ArcGIS Pro DAML files and `CommandSearchTerms.xml` files provide the
authoritative IDs registered by installed Esri extensions. For example,
`esri_core_saveProjectButton` is defined in the shipped Core DAML. Protocol
v0.23 uses that command only for read-only wrapper inspection during validation;
it never executes it.

## Design conclusion

Typed bridge actions remain preferable for data, map, layout and analytical
operations because their parameters and side effects can be validated. The
generic Framework layer is a controlled fallback for UI-only commands, panes,
tabs, tools and Backstage pages. Exact allowlists are required because the same
Framework APIs can reach harmless navigation commands as well as save, edit,
publish or extension-specific operations.

This layer materially reduces future Add-in rebuilds: after an ID is reviewed,
many additional Pro UI capabilities can be enabled by MCP configuration alone.
Functions requiring typed SDK objects, complex callbacks, custom interaction,
or source-data mutation still require dedicated audited actions.
