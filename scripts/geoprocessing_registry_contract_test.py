from __future__ import annotations

import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import ArcGISMCP, TOOLS


def main() -> int:
    names = {tool["name"] for tool in TOOLS}
    required = {
        "search_arcgis_geoprocessing_registry",
        "describe_arcgis_geoprocessing_tool",
    }
    if not required.issubset(names):
        raise RuntimeError(f"Geoprocessing registry tools are not registered: {required - names}")

    server = ArcGISMCP(ROOT / "config.json")
    search = server.call_tool(
        "search_arcgis_geoprocessing_registry",
        {"query": "AddJoin", "toolboxes": ["management"], "max_results": 10},
    )
    if int(search.get("installed_tool_count") or 0) < 1000:
        raise RuntimeError(f"ArcGIS geoprocessing catalog is unexpectedly small: {search}")
    result = next(
        (item for item in search.get("results") or [] if item.get("tool") == "management.AddJoin"),
        None,
    )
    if result is None:
        raise RuntimeError(f"management.AddJoin was not found: {search}")
    if result.get("safety", {}).get("generic_runner_toolbox_allowed") is not True:
        raise RuntimeError(f"AddJoin should be discoverable through the generic runner: {result}")
    if search.get("source_data_modified") is not False:
        raise RuntimeError("Geoprocessing search did not affirm source_data_modified=false")

    description = server.call_tool(
        "describe_arcgis_geoprocessing_tool",
        {"tool": "management.AddJoin", "include_documentation": True},
    )
    parameters = description.get("parameters") or []
    if description.get("legacy_name") != "AddJoin_management" or len(parameters) < 4:
        raise RuntimeError(f"AddJoin parameter inspection is incomplete: {description}")
    if parameters[0].get("name") != "in_layer_or_view" or not parameters[0].get("display_name"):
        raise RuntimeError(f"Localized parameter metadata is missing: {parameters[0]}")
    if "Joins a layer" not in str(description.get("documentation") or ""):
        raise RuntimeError("Full ArcPy tool documentation was not returned")

    server._validate_tool_name("management.AddJoin")
    for guarded in ("management.AddField", "management.Append"):
        try:
            server._validate_tool_name(guarded)
        except PermissionError:
            pass
        else:
            raise RuntimeError(f"Guarded source/schema tool was not blocked: {guarded}")

    print(
        f"geoprocessing registry contract ok: indexed {search['installed_tool_count']} installed tools, "
        "returned localized parameter metadata, and enforced source/schema gates"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
