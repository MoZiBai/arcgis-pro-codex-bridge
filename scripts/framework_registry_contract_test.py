from __future__ import annotations

import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import ArcGISMCP, TOOLS


def main() -> int:
    names = {tool["name"] for tool in TOOLS}
    if "search_arcgis_framework_registry" not in names:
        raise RuntimeError("Framework registry search tool is not registered")
    server = ArcGISMCP(ROOT / "config.json")
    exact = server.call_tool(
        "search_arcgis_framework_registry",
        {"query": "esri_core_saveProjectButton", "max_results": 10},
    )
    results = exact.get("results") or []
    match = next(
        (item for item in results if item.get("id") == "esri_core_saveProjectButton"),
        None,
    )
    if match is None:
        raise RuntimeError(f"Known Core command was not indexed: {exact}")
    if "button" not in match.get("kinds", []):
        raise RuntimeError(f"Known Core command has the wrong kind: {match}")
    if "project_or_file_write" not in match.get("risk_hints", []):
        raise RuntimeError(f"Save command was not flagged for write risk: {match}")
    if exact.get("source_data_modified") is not False:
        raise RuntimeError("Registry search did not affirm source_data_modified=false")

    localized = server.call_tool(
        "search_arcgis_framework_registry",
        {"query": "保存工程", "kinds": ["button"], "max_results": 20},
    )
    if not any(item.get("id") == "esri_core_saveProjectButton" for item in localized.get("results") or []):
        raise RuntimeError(f"Localized caption search did not find the Core save command: {localized}")

    print(
        "framework registry contract ok: indexed "
        f"{exact.get('registry_size')} exact Framework IDs with localized search and risk hints"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
