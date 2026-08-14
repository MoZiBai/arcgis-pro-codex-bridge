# ArcGIS Pro 3.6.3 SDK audit for protocol v0.24

The v0.24 audit concentrated on reducing future rebuilds while retaining typed
SDK actions for operations that require native object semantics.

## Broad reusable surfaces

The bridge now has three complementary long-tail layers:

1. The Framework registry indexes 5,952 installed command, tool, pane, tab,
   Backstage and related IDs. Exact-ID execution remains disabled by default.
2. The ArcPy registry indexes 1,977 installed geoprocessing tools and exposes
   localized parameter metadata before execution.
3. Bounded CIM inspection and merge patching covers display properties that do
   not justify another dedicated wrapper, while protecting structural and data
   connection paths by default.

These layers do not make every ArcGIS Pro operation safe to execute
generically. Source editing, schema changes, deletion, publishing, workflow
changes and external UI actions retain independent gates.

## Public SDK findings implemented in v0.24

- `CIMPage.Guides` and `CIMGuide` provide a stable typed layout-guide lifecycle.
- `MapView` exposes map/client/screen coordinate conversion methods; runtime
  testing showed that view UI operations must be dispatched through the WPF UI
  dispatcher.
- Public `EPSFormat` and `AIXFormat` classes work through the same native
  `Layout.Export` and `MapView.Export` paths as existing formats.
- `MapView.ShowPopup` and `ClosePopups` are UI-thread-only despite related map
  member resolution occurring on the main CIM thread.

## Deliberately not treated as generic typed execution

Many chart editors, print panes, metadata editor controls and extension-specific
view models visible through reflection are internal UI implementation types,
not stable public automation contracts. They remain reachable only through an
audited public API, a validated geoprocessing tool, protected CIM, or an exact
Framework allowlist entry.
