# ArcGIS Pro 3.6.3 SDK audit for protocol v0.21

Reflection over the installed `ArcGIS.Desktop.Mapping` assembly confirms the
public `MapView` surface used by this batch:

- `IsReady`, `DrawingPaused`, `CurrentMapTOCContent`, `GetViewSize`, camera
  history and all selected map/elevation/member inventory;
- `SelectLayers`, `SelectStandaloneTables`, elevation selection, TOC clearing,
  redraw and draw cancellation;
- fixed zoom, camera history, selection pan/zoom, bounded feature flashing and
  native popup lifecycle;
- `CaptureThumbnail` returning a WPF `BitmapSource` for controlled local export.

The public `Map` surface provides auto-fill state and asynchronous fill, clear
and cancel operations for the feature cache. These methods affect only cached
display content.

## Deferred from this audit

- physical printing is intentionally not exposed because it creates external
  device state and needs printer enumeration/confirmation;
- custom popup HTML/media builders need bounded content and URI policies;
- overlay controls and custom UI injection are application-extension concerns,
  not general GIS automation actions;
- scene 3D-object export needs an authoritative extension/format contract before
  being exposed as a path-writing action.
