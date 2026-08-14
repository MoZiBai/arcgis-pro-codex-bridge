# ArcGIS Codex live bridge protocol v0.28.2

Protocol v0.28.2 is a maintenance release for ArcGIS Pro 3.6. It does not add
new MCP tools or Add-in actions. The v0.28.1 queue envelope, session binding,
cancellation, and idempotency behavior remain unchanged.

## Installation ownership

The installation manifest now records the exact Add-in directory, package path,
version, and SHA-256. Offline status validates those values. Uninstall removes
only the Add-in package and global MCP registration owned by the selected
installation; a registration pointing to another install root is preserved.

Installation is transactional across the runtime, private config, manifest,
MCP registration, locator, and Add-in package. A late failure restores the
previous state. `-SkipAddin` rejects an already installed Add-in whose ID or
version does not match the MCP protocol.

## Policy parity

`live_execute_batch` and direct live tools use the same action allowlist,
path normalization, and source-edit safety gates. The protocol contract checks
that every public Add-in action except the internal worker and nested batch
action is available to the batch policy.

## Status privacy

`arcgis_status` returns only product, version, build, license, and Python-version
metadata. Installer identity, installer source directory, installation dates,
and the full Python executable path are intentionally omitted.

## Compatibility

This protocol supports ArcGIS Pro 3.6 only. ArcMap and ArcGIS Desktop 10.2–10.8
remain outside the current compatibility boundary.
