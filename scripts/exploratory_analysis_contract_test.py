from __future__ import annotations

import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import TOOLS


def main() -> int:
    tool = next(
        (item for item in TOOLS if item["name"] == "live_manage_exploratory_analysis"),
        None,
    )
    if tool is None:
        raise RuntimeError("Exploratory-analysis MCP tool is not registered")

    properties = tool["inputSchema"]["properties"]
    if set(properties["operation"]["enum"]) != {"inspect", "add", "remove", "clear"}:
        raise RuntimeError("Exploratory-analysis operation surface is incomplete")
    if properties["targets"].get("maxItems") != 1000:
        raise RuntimeError("Line-of-sight targets must remain bounded at 1000")

    source = (
        ROOT / "addin" / "ArcGISCodexBridgeAddin" / "ExploratoryAnalysisActions.cs"
    ).read_text(encoding="utf-8")
    required = {
        "view.GetExploratoryAnalysisCollection()",
        "view.AddExploratoryAnalysisAsync(analysis)",
        "view.RemoveExploratoryAnalysisAsync",
        "await view.AddExploratoryAnalysisAsync(analysis)",
        "await view.RemoveExploratoryAnalysisAsync",
        "confirm_remove=true is required",
        "confirm_clear=true is required",
        ".Take(1000)",
        "double.IsFinite",
        "source_data_modified = false",
        "project_saved = false",
    }
    missing = sorted(fragment for fragment in required if fragment not in source)
    if missing:
        raise RuntimeError(f"Exploratory-analysis safety contract is incomplete: {missing}")
    forbidden = {"Geodatabase", "File.Delete", "SaveAsync", "SaveEdits"}
    present = sorted(fragment for fragment in forbidden if fragment in source)
    if present:
        raise RuntimeError(f"Exploratory analysis unexpectedly references persistent mutation APIs: {present}")
    if "GetAwaiter().GetResult()" in source:
        raise RuntimeError("Exploratory analysis must not synchronously block ArcGIS async APIs inside QueuedTask")

    print(
        "exploratory analysis contract ok: active-scene Viewshed/ViewDome/LineOfSight "
        "management is bounded, confirmed for removal, unsaved, and dataset-safe"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
