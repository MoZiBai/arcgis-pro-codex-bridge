# Installation and lifecycle

This release targets **ArcGIS Pro 3.6 on Windows 10/11**. ArcMap and ArcGIS
Desktop 10.2–10.8 use a different Python, ArcObjects, and add-in architecture;
they are intentionally unsupported by this installer and will be addressed by
a future compatibility adapter.

## What the installer owns

The default installation root is:

```text
%LOCALAPPDATA%\ArcGISCodexBridge
```

It contains a stable MCP runtime, a machine-private `config.json`, a probe
utility, a versioned installation manifest, bounded data/output directories,
and the live-bridge queue. The installer also:

- builds and installs the ArcGIS Pro SDK Add-in;
- writes one `mcp_servers.arcgis-pro` table to the user's global
  `~/.codex/config.toml`;
- preserves unrelated Codex settings and MCP servers;
- creates a timestamped configuration backup before changing an existing
  global config;
- leaves source editing, schema changes, deletion, publishing, external
  workflow actions, and generic Framework UI control disabled.

The installed MCP points to the stable runtime rather than the Git clone, so
moving or deleting the source checkout does not break the registration.

## Install

From a cloned repository:

```powershell
.\scripts\install.ps1 `
  -AllowedDataRoot 'C:\GISData' `
  -OutputRoot 'C:\GISOutput'
```

If ArcGIS Pro is not installed under `C:\Program Files\ArcGIS\Pro`, pass
`-ArcGISProInstallDir` or set `ARCGIS_PRO_INSTALL_DIR`. The installer also
checks the ArcGIS Pro registry keys and an existing local development config.
Pass `-MSBuild` when Visual Studio Build Tools cannot be discovered by
`vswhere`. Existing development settings can be migrated explicitly with
`-ImportConfig .\config.json`; because this imports the complete local policy,
review all safety switches first.

For an MCP-runtime-only update that must not rebuild or reinstall the Add-in:

```powershell
.\scripts\install.ps1 -SkipAddin
```

After installation, restart Codex. Restart ArcGIS Pro when the Add-in changed.
The globally registered server is then visible in Codex/ChatGPT MCP settings
and third-party global MCP managers such as Codex++.

## Status and diagnostics

The normal status check verifies the manifest, stable files, global MCP entry,
ArcGIS Pro 3.6, Add-in package, MCP initialization, server instructions, and
required tool discovery without importing ArcPy:

```powershell
.\scripts\status.ps1
```

The deeper diagnostic also starts the ArcPy worker:

```powershell
.\scripts\doctor.ps1
```

If Windows Security blocks `_arcgisscripting.pyd`, first inspect Protection
History and repair ArcGIS Pro from its official installer. Do not broadly
exclude the ArcGIS installation directory; allow an exact file only after its
Esri signature has been verified.

## Uninstall

Remove the global MCP registration and the owned installation directory:

```powershell
.\scripts\uninstall.ps1
```

The Add-in is retained by default. Remove its exact ID-scoped installation too
only when explicitly requested:

```powershell
.\scripts\uninstall.ps1 -RemoveAddin
```

Restart Codex after uninstalling, and restart ArcGIS Pro if the Add-in was
removed.

## MCP routing boundary

The server returns MCP `instructions` during initialization. Codex is told to
use this server only for requests clearly involving ArcGIS Pro, ArcPy, GIS
datasets, spatial analysis, maps, layers, layouts, cartography,
geoprocessing, or the currently open ArcGIS Pro project. It is explicitly told
not to call the server for unrelated coding, ordinary files, web research,
office documents, or general questions.

This is the standard MCP routing mechanism, not a deterministic keyword hook.
The host model makes the final selection from the request, server instructions,
and individual tool descriptions. Safety gates still enforce what a selected
tool may do.
