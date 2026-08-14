from __future__ import annotations

import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import ArcGISMCP, TOOLS


def main() -> int:
    names = {tool["name"] for tool in TOOLS}
    if "search_arcgis_sdk_registry" not in names:
        raise RuntimeError("ArcGIS SDK registry search tool is not registered")

    server = ArcGISMCP(ROOT / "config.json")
    result = server.call_tool(
        "search_arcgis_sdk_registry",
        {
            "query": "MapToClient",
            "assemblies": ["ArcGIS.Desktop.Mapping"],
            "max_results": 20,
        },
    )
    matches = result.get("results") or []
    map_view = next(
        (item for item in matches if item.get("type") == "ArcGIS.Desktop.Mapping.MapView"),
        None,
    )
    if map_view is None:
        raise RuntimeError(f"MapView.MapToClient was not found in the installed SDK: {result}")
    if not any("MapToClient(" in method for method in map_view.get("methods") or []):
        raise RuntimeError(f"MapView method signatures are incomplete: {map_view}")
    if result.get("source_data_modified") is not False or result.get("project_modified") is not False:
        raise RuntimeError("SDK registry search did not affirm its read-only behavior")
    if result.get("execution_available") is not False:
        raise RuntimeError("SDK registry search must not expose unrestricted reflection execution")

    print(
        "sdk registry contract ok: found ArcGIS.Desktop.Mapping.MapView.MapToClient "
        "with public signatures and read-only safety metadata"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
