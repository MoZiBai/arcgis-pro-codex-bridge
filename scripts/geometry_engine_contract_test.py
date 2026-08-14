from __future__ import annotations

import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import TOOLS


def main() -> int:
    tool = next((item for item in TOOLS if item["name"] == "live_geometry_engine"), None)
    if tool is None:
        raise RuntimeError("GeometryEngine MCP tool is not registered")
    operations = set(tool["inputSchema"]["properties"]["operation"]["enum"])
    required = {
        "project", "buffer", "geodesic_buffer", "intersection", "union",
        "difference", "contains", "within", "intersects", "relate", "distance",
        "geodesic_distance", "area", "geodesic_area", "length", "geodesic_length",
        "simplify", "generalize", "densify_by_length", "move", "rotate", "scale",
        "nearest_point", "multipart_to_singlepart",
    }
    if not required.issubset(operations):
        raise RuntimeError(f"GeometryEngine operation surface is incomplete: {required - operations}")
    source = (
        ROOT / "addin" / "ArcGISCodexBridgeAddin" / "GeometryEngineActions.cs"
    ).read_text(encoding="utf-8")
    for fragment in (
        "GeometryEngine.Instance.ImportFromJson",
        "GeometryEngine.Instance.ImportFromWKT",
        "text.Length > 2 * 1024 * 1024",
        "source_data_modified = false",
        "project_modified = false",
        "derived_dataset_created = false",
    ):
        if fragment not in source:
            raise RuntimeError(f"GeometryEngine safety/format contract is missing: {fragment}")
    if "File." in source or "Geodatabase" in source or "Table" in source:
        raise RuntimeError("In-memory GeometryEngine action unexpectedly references persistent data APIs")
    print(f"geometry engine contract ok: {len(operations)} bounded in-memory operations with no dataset access")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
