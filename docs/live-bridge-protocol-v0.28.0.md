# ArcGIS Codex live bridge protocol v0.28.0

Protocol v0.28.0 adds a bounded ArcPy worker host action and keeps every v0.27.3
mapping unchanged.

## ArcPy worker fallback

- `run_arcpy_worker` is an internal Add-in action used only after the normal
  ArcPy subprocess fails with Windows error 225 or an equivalent
  virus/potentially-unwanted-software block.
- The Add-in derives Python from the running ArcGIS Pro installation. Callers
  cannot select an arbitrary executable.
- `worker_path` must resolve to the installed
  `%LOCALAPPDATA%\ArcGISCodexBridge\runtime\worker.py`; paths outside that root
  and other filenames are rejected.
- The request remains one of the existing allowlisted worker actions. All MCP
  path validation, output-root enforcement, destructive/source/schema/
  publishing gates, and explicit confirmation requirements run before the
  request reaches the Add-in.
- The worker timeout is bounded to 1–3,600 seconds. Cancellation terminates its
  complete child process tree.
- No Defender exclusion, firewall rule, service, scheduled task, or elevated
  helper is created.

The fallback requires ArcGIS Pro to be open with the v0.28.0 Add-in loaded. If
the application is closed, the original ArcPy security error and the live
bridge failure are both returned for diagnosis.

## Compatibility

This protocol and installer support ArcGIS Pro 3.6 only. ArcMap and ArcGIS
Desktop 10.2–10.8 remain outside the current compatibility boundary.
