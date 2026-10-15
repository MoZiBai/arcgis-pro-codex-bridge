from __future__ import annotations

from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
LAYOUT_SOURCE = ROOT / "addin" / "ArcGISCodexBridgeAddin" / "LayoutActions.cs"
CARTOGRAPHY_SOURCE = ROOT / "addin" / "ArcGISCodexBridgeAddin" / "CartographyActions.cs"
SERVER_SOURCE = ROOT / "src" / "arcgis_mcp" / "server.py"


def main() -> int:
    layout = LAYOUT_SOURCE.read_text(encoding="utf-8")
    cartography = CARTOGRAPHY_SOURCE.read_text(encoding="utf-8")
    server = SERVER_SOURCE.read_text(encoding="utf-8")
    required_layout = {
        "ValidateAsync",
        "GetElementsAsFlattenedList",
        "outside_page",
        "minimum_overlap_area",
        "missing_surround",
        "empty_legend",
        "too_small",
        "map_frame_overlap",
        "legend_order",
        "source_data_modified = false",
        "project_modified = false",
        "background",  # native legend background remains explicit opt-in
    }
    missing = sorted(value for value in required_layout if value not in layout and value not in cartography and value not in server)
    if missing:
        raise RuntimeError(f"Layout validation contract missing: {missing}")
    if 'BridgeSupport.Bool(legendConfig, "background", false)' not in layout:
        raise RuntimeError("Native legend background is not transparent by default")
    for marker in (
        '"name": "live_add_map_surround"',
        '"name": "live_configure_map_surround"',
        '"layer_names"',
        'FilterLegendItemsAsync',
        'definition.AutoAdd = false',
    ):
        if marker not in server and marker not in layout:
            raise RuntimeError(f"Native legend filtering contract missing: {marker}")
    for marker in (
        '"name": "live_validate_layout"',
        '"validate_layout"',
        'live_validate_layout',
        'validate_before_export',
    ):
        if marker not in server:
            raise RuntimeError(f"MCP layout validation route missing: {marker}")
    print("layout validation contract ok: native geometry checks, surrounds, legend state, and transparent default")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
