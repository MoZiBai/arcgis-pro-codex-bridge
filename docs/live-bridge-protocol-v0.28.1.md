# ArcGIS Codex live bridge protocol v0.28.1

Protocol v0.28.1 is a safety and runtime maintenance update for ArcGIS Pro 3.6.
It keeps the v0.28.0 tool and action mappings while hardening request delivery,
cancellation, installation discovery, and public release artifacts.

## Queue envelope and identity binding

Every queue job includes a unique job ID, an idempotency key, creation and
expiration times, plus the expected ArcGIS Pro session and project path. The
MCP server performs a status handshake before a project-bound action. The
Add-in rejects an action when it has expired, ArcGIS Pro has restarted, or a
different project is open.

`status` and `capabilities` are the only actions that do not require an existing
session/project binding. Every result reports `bridge_session_id` and
`project_path`, allowing the MCP server to refresh its binding after an explicit
Save As operation.

## Cancellation and duplicate protection

- The MCP stdio loop continues reading while tool calls run in a bounded thread
  pool, so `notifications/cancelled` can be handled during a long operation.
- A cancelled or timed-out live call removes an unclaimed job and writes a
  cancellation marker. The Add-in checks that marker before dispatch and while
  a cancellable action is running.
- Completed results are cached by idempotency key for 24 hours. A repeated queue
  delivery returns the cached result and never repeats the ArcGIS Pro mutation.
- Direct ArcPy workers are polled for MCP cancellation and timeout; cancellation
  terminates the process tree.

## Installation and release privacy

The fixed per-user locator
`%LOCALAPPDATA%\ArcGISCodexBridge\install-location.json` records the real
installation root. The Add-in uses it for both the queue config and the bounded
`runtime\worker.py` path, so a custom `-InstallRoot` remains functional.

Release builds disable PDB/debug-symbol generation. Lifecycle validation opens
the `.esriAddinX`, rejects PDB entries, and scans packaged binaries for source
checkout paths, user-profile paths, credentials, private keys, and email
addresses.

## Compatibility

This protocol supports ArcGIS Pro 3.6 only. ArcMap and ArcGIS Desktop 10.2–10.8
remain outside the current compatibility boundary.
