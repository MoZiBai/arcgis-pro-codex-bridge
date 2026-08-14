from __future__ import annotations

import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import TOOLS


def main() -> int:
    tools = {item["name"]: item for item in TOOLS}
    required = {"live_configure_specialized_renderer", "live_inspect_printers"}
    if not required.issubset(tools):
        raise RuntimeError(f"Specialized rendering/printing tools are missing: {required - set(tools)}")
    renderer_values = {
        value for value in tools["live_configure_specialized_renderer"]["inputSchema"]
        ["properties"]["renderer_type"]["enum"] if value is not None
    }
    expected = {
        "face", "edge", "node", "contour", "face_elevation", "face_aspect",
        "node_elevation", "terrain_points", "dirty_area", "breakline",
        "StretchRenderer", "ClassBreaksRenderer", "UniqueValueRenderer", "RGBRenderer",
    }
    if not expected.issubset(renderer_values):
        raise RuntimeError(f"Specialized renderer surface is incomplete: {expected - renderer_values}")

    source = (
        ROOT / "addin" / "ArcGISCodexBridgeAddin" / "SpecializedRenderingActions.cs"
    ).read_text(encoding="utf-8")
    required_source = {
        "TinFaceRendererDefinition", "TinEdgeRendererDefintion", "TinNodeRendererDefinition",
        "TinContourRendererDefinition", "TinFaceClassBreaksRendererDefinition",
        "TinFaceClassBreaksAspectRendererDefinition", "TinNodeClassBreaksRendererDefinition",
        "TerrainPointClassBreaksRendererDefinition", "TerrainDirtyAreaRendererDefinition",
        "TinBreaklineRendererDefinition", "PointCloudRendererDefinition",
        "SetEyeDomeLightingEnabled", "confirm_remove=true is required",
        "printing_executed = false", "external_state_modified = false",
        "source_data_modified = false", "project_saved = false",
        "2 * 1024 * 1024",
    }
    missing = sorted(value for value in required_source if value not in source)
    if missing:
        raise RuntimeError(f"Specialized rendering safety contract is incomplete: {missing}")
    if ".Print(" in source or "SaveAsync" in source or "SaveEdits" in source:
        raise RuntimeError("Read-only printer inspection or renderer adapter unexpectedly invokes persistent/external mutation")
    print("specialized rendering contract ok: TIN/terrain/point-cloud adapters are bounded and printer inspection cannot print")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
