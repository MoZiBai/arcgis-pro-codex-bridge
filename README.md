<div align="right">

English | <a href="README.zh-CN.md">简体中文</a>

</div>

# ArcGIS Pro Bridge for Codex

Connect Codex to the currently open ArcGIS Pro project through a local MCP
server, ArcPy, and an ArcGIS Pro SDK add-in—without mouse or keyboard automation.

[![ArcGIS Pro](https://img.shields.io/badge/ArcGIS%20Pro-3.6-2C7AC3)](https://www.esri.com/en-us/arcgis/products/arcgis-pro/overview)
[![Protocol](https://img.shields.io/badge/bridge%20protocol-0.28.2-486581)](docs/live-bridge-protocol-v0.28.2.md)
[![Safety](https://img.shields.io/badge/source%20editing-disabled%20by%20default-2E8B57)](#security-boundaries)
[![License](https://img.shields.io/badge/license-Apache--2.0-D22128)](LICENSE)

The current release exposes 253 MCP tools. Of these, 236 use the add-in to work
with the active ArcGIS Pro session. The server can be registered as a standard
stdio MCP in Codex or used by another MCP-compatible client.

## Key capabilities

- Data discovery and description: scan folders and inspect vector, raster, and
  table fields, coordinate systems, extents, and record counts.
- Geoprocessing: run guarded ArcPy tools and search more than 1,000 locally
  installed geoprocessing tools with parameter and safety metadata.
- Projects and maps: inventory projects; create, duplicate, rename, and open
  maps; manage layers, standalone tables, bookmarks, and cameras.
- Data queries: bounded attribute queries, summaries, selections, spatial
  queries, relationships, and attachment inventories.
- Symbology and labeling: vector and raster renderers, visual variables,
  display filters, Standard/Maplex labels, and layer elevation.
- Terrain and 3D: elevation tinting, independent hillshade, LAS and voxel
  layers, scene elevation surfaces, and exploratory analysis.
- Academic cartography: layouts, map frames, native legends, scale bars, north
  arrows, grids, color bars, extent indicators, and map series.
- Export: PDF, PNG, JPEG, TIFF, SVG, SVGZ, EPS, AIX, reports,
  presentations, and map series.
- Advanced project objects: Catalog items, reports, presentations, Task
  Assistant, Workflow Manager, Knowledge Graph, and CIM objects.
- Extension discovery: read-only search over the ArcGIS Pro SDK, DAML/Framework,
  and geoprocessing registries.

See [capability-matrix.md](docs/capability-matrix.md) for the complete inventory.

## Architecture

```text
Codex or another MCP client
  │ stdio MCP
  ▼
Python MCP server ── ArcPy worker
  │ bounded local queue
  ▼
ArcGIS Pro SDK add-in ── currently open ArcGIS Pro project
```

- The MCP server owns tool definitions, path validation, configuration, and
  safety policy.
- The ArcPy worker handles data discovery, dataset description, and background
  geoprocessing.
- The SDK add-in works with the active project and UI objects inside the
  ArcGIS Pro process.
- The add-in loads automatically and consumes the local queue. No Python
  Toolbox or separate startup step is required.

If the Codex Windows sandbox cannot load ArcPy native modules because Windows
Security returns error 225, the server can forward the same validated worker
request to a restricted child process launched by the active ArcGIS Pro process.
The fallback cannot select an arbitrary executable or worker path and does not
add a Microsoft Defender exclusion.

## Requirements

- Windows 10 or Windows 11 x64
- ArcGIS Pro 3.6
- Python 3.10 or newer for the MCP server
- Codex or another client with stdio MCP support

Installing the GitHub Release does not require Visual Studio, MSBuild, or the
.NET SDK. Rebuilding the ArcGIS Pro add-in from source requires the .NET 8 SDK
and Visual Studio 2022 Build Tools.

ArcMap / ArcGIS Desktop 10.2–10.8 use different Python, ArcObjects, and add-in
architectures and are not supported by this release.

## Installation

Download these files from GitHub Releases:

```text
arcgis-pro-codex-bridge-0.28.2-win-x64.zip
arcgis-pro-codex-bridge-0.28.2-win-x64.zip.sha256
```

Verify the SHA-256 checksum, extract the ZIP, and run in PowerShell:

```powershell
.\scripts\install.ps1 `
  -AllowedDataRoot 'C:\GISData' `
  -OutputRoot 'C:\GISOutput'
```

The installer:

1. Verifies ArcGIS Pro 3.6 and the system Python runtime.
2. Installs the stable MCP runtime under `%LOCALAPPDATA%\ArcGISCodexBridge`.
3. Creates a machine-private safety configuration.
4. Validates and installs the prebuilt `.esriAddinX` package.
5. Registers the `arcgis-pro` MCP in the user-level `~/.codex/config.toml`.

Use `-ArcGISProInstallDir` when ArcGIS Pro is installed in a non-default
location. Restart Codex after installation. Restart ArcGIS Pro whenever the
add-in changes.

See [installation.md](docs/installation.md) for all options and lifecycle details.

## Status and diagnostics

Run the normal check without importing ArcPy:

```powershell
.\scripts\status.ps1
```

Run the full diagnostic, including the ArcPy worker:

```powershell
.\scripts\doctor.ps1
```

The checks cover the installation manifest, versions, global MCP registration,
add-in package, server initialization, tool discovery, and the ArcPy runtime.
If Windows Security reports an ArcGIS DLL, inspect Protection History and repair
ArcGIS Pro from the official Esri installer. Do not broadly exclude the ArcGIS
installation directory.

## Configuration

With the default install root, the private configuration is stored at:

```text
%LOCALAPPDATA%\ArcGISCodexBridge\config\config.json
```

With `-InstallRoot`, it is stored under `<InstallRoot>\config\config.json`; the
fixed per-user locator lets the Add-in find that custom root.

Key settings:

| Setting | Purpose | Default |
| --- | --- | --- |
| `allowed_data_roots` | Data roots that may be read | installation `data` directory |
| `output_root` | Root for every derived output | installation `outputs` directory |
| `live_bridge_queue` | Local MCP-to-add-in queue | installation `queue` directory |
| `allow_source_editing` | Permit source-data edits | `false` |
| `allow_destructive_tools` | Permit delete or truncate operations | `false` |
| `allow_schema_changes` | Permit field and schema changes | `false` |
| `allow_external_publishing` | Permit external publishing | `false` |
| `allow_framework_ui_control` | Permit generic Framework UI control | `false` |

See [config.example.json](config.example.json) for the development template.
Safety policy can be explicitly hot-reloaded. Data paths and runtime locations
require an MCP restart.

## Security boundaries

- Reads are limited to `allowed_data_roots`; derived files must be written
  under `output_root`.
- Delete, truncate, source editing, schema changes, publishing, and external
  workflow actions are disabled by default.
- Generic ArcPy execution and specialized tools share the same safety gates.
- Framework control requires both the master switch and an exact ID allowlist.
- Project changes prefer a new `.aprx` copy instead of overwriting the source.
- The ArcPy fallback runs only after an explicit Windows Security load error;
  ordinary worker failures do not trigger it.
- The fallback worker is fixed to `runtime\worker.py` under the stable install,
  and Python is derived from the current ArcGIS Pro installation.
- The installer creates no Defender exclusions, services, scheduled tasks,
  firewall rules, or elevated helpers.
- `config.json`, GIS data, outputs, queues, logs, and build artifacts are
  excluded from version control.

## Building from source

When no `prebuilt` package is present, the source installer builds the add-in:

```powershell
.\scripts\install.ps1 `
  -ArcGISProInstallDir 'C:\Program Files\ArcGIS\Pro'
```

Development steps can also be run separately:

```powershell
.\scripts\build-addin.ps1
.\scripts\install-addin.ps1
.\scripts\register-mcp.ps1
```

Build a GitHub Release package with:

```powershell
.\scripts\build-release.ps1 `
  -ArcGISProInstallDir 'C:\Program Files\ArcGIS\Pro'
```

The `dist` directory receives a versioned folder, ZIP archive, per-file SHA-256
manifest, and ZIP checksum file.

## Testing

Core protocol and safety checks:

```powershell
python .\scripts\protocol_contract_test.py
python .\scripts\version_contract_test.py
python .\scripts\safety_contract_test.py
python .\scripts\runtime_safety_contract_test.py
python .\scripts\arcpy_worker_fallback_contract_test.py
python .\scripts\queue_and_concurrency_contract_test.py
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\installation_contract_test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\installation_lifecycle_test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release_lifecycle_test.ps1
```

The Release lifecycle test extracts the package in a temporary directory,
verifies every hash, scans for private information, installs without MSBuild,
discovers all 253 tools, and uninstalls without touching real GIS data or the
actual add-in directory.

## Uninstalling

```powershell
.\scripts\uninstall.ps1
```

The add-in is retained by default. Remove it explicitly with:

```powershell
.\scripts\uninstall.ps1 -RemoveAddin
```

## Documentation

- [Installation and lifecycle](docs/installation.md)
- [Protocol v0.28.2](docs/live-bridge-protocol-v0.28.2.md)
- [Live bridge design](docs/live-bridge-design.md)
- [Capability matrix](docs/capability-matrix.md)
- [SDK coverage audit](docs/sdk-coverage-audit-v0.27.md)
- [Release notes v0.28.2](docs/release-v0.28.2.md)

## License

Copyright 2026 MoZiBai.

Licensed under the [Apache License 2.0](LICENSE).

ArcGIS Pro and the ArcGIS Pro SDK are Esri products governed by Esri's own
license terms; they are dependencies and are not relicensed by this repository.
