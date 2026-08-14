from __future__ import annotations

import json
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import ArcGISMCP


def main() -> int:
    server = ArcGISMCP(ROOT / "config.json")
    server.live_timeout = min(server.live_timeout, 120)
    status = server._run_live("status", {})
    if status.get("protocol_version") != "0.12.0":
        raise RuntimeError(
            f"Expected protocol 0.12.0, got {status.get('protocol_version')}"
        )
    capabilities = server._run_live("capabilities", {})
    actions = set(capabilities.get("actions") or [])
    required = {
        "list_extent_indicators",
        "add_extent_indicator",
        "configure_extent_indicator",
        "remove_extent_indicators",
        "configure_map_clipping",
        "add_dynamic_text",
    }
    if len(actions) < 140 or not required.issubset(actions):
        raise RuntimeError(
            f"Expected at least 140 actions including v0.12 additions; got {len(actions)}"
        )

    marker = str(int(time.time()))
    overview_map = f"CodexV012Overview_{marker}"
    detail_map = f"CodexV012Detail_{marker}"
    layout_name = f"CodexV012Layout_{marker}"
    synthetic_gdb = (
        ROOT
        / "outputs"
        / "宜昌市测试数据"
        / "yichang_synthetic_20260730_230117.gdb"
    )
    districts = synthetic_gdb / "模拟区县"
    residents = synthetic_gdb / "模拟居民点"
    if not synthetic_gdb.exists():
        raise FileNotFoundError(f"Synthetic validation input is missing: {synthetic_gdb}")

    summary: dict[str, object] = {
        "handshake": {
            "addin_version": status.get("addin_version"),
            "protocol_version": status.get("protocol_version"),
            "action_count": len(actions),
        },
        "source_data_modified": False,
        "project_saved": False,
    }
    created_maps: list[str] = []
    layout_created = False
    try:
        for map_name in (overview_map, detail_map):
            server._run_live("create_map", {"map_name": map_name, "open_map": False})
            created_maps.append(map_name)
            server._run_live(
                "add_data",
                {
                    "map_name": map_name,
                    "data_paths": [str(districts), str(residents)],
                },
            )

        clipping_enabled = server._run_live(
            "configure_map_clipping",
            {
                "map_name": detail_map,
                "mode": "CustomShape",
                "clipping_layer_name": "模拟区县",
                "excluded_layer_names": ["模拟居民点"],
                "border_visible": True,
                "border_color": "#B52A2A",
                "border_width": 1.2,
            },
        )
        if clipping_enabled.get("clipping_mode") != "CustomShape":
            raise RuntimeError(f"Custom clipping did not apply: {clipping_enabled}")

        summary["create_layout"] = server._run_live(
            "create_layout",
            {
                "layout_name": layout_name,
                "map_name": overview_map,
                "map_frame_name": "Overview Frame",
                "legend": False,
                "north_arrow": False,
                "scale_bar": False,
                "open_layout": False,
            },
        )
        layout_created = True
        summary["add_detail_frame"] = server._run_live(
            "add_map_frame",
            {
                "layout_name": layout_name,
                "map_name": detail_map,
                "map_frame_name": "Detail Frame",
                "bounds": {"xmin": 18, "ymin": 2, "xmax": 28, "ymax": 10},
                "extent_layer_name": "模拟区县",
                "map_frame_border": True,
            },
        )
        indicator_added = server._run_live(
            "add_extent_indicator",
            {
                "layout_name": layout_name,
                "map_frame_name": "Overview Frame",
                "source_map_frame_name": "Detail Frame",
                "indicator_name": "Validation Extent",
                "indicator_type": "Frame",
                "frame_color": "#C3362B",
                "frame_width": 1.5,
                "leader_type": "LineToNearestPoint",
            },
        )
        indicator_configured = server._run_live(
            "configure_extent_indicator",
            {
                "layout_name": layout_name,
                "map_frame_name": "Overview Frame",
                "indicator_name": "Validation Extent",
                "new_name": "Validation Extent Updated",
                "symbolize_exterior": False,
                "avoid_label_conflict": True,
                "collapse_size": 5,
                "leader_type": "LineToMidpoint",
            },
        )
        indicator_inventory = server._run_live(
            "list_extent_indicators",
            {
                "layout_name": layout_name,
                "map_frame_name": "Overview Frame",
            },
        )
        indicator_names = {
            item.get("name")
            for item in indicator_inventory.get("extent_indicators", [])
        }
        if "Validation Extent Updated" not in indicator_names:
            raise RuntimeError(
                f"Extent indicator did not round-trip: {indicator_names}"
            )

        dynamic_text = server._run_live(
            "add_dynamic_text",
            {
                "layout_name": layout_name,
                "element_name": "Validation Dynamic Date",
                "text": "Generated: <dyn type=\"date\" format=\"yyyy-MM-dd HH:mm\"/>",
                "x": 2,
                "y": 1,
                "text_symbol": {"size": 8, "color": "#29343A"},
            },
        )
        if not dynamic_text.get("contains_dynamic_text"):
            raise RuntimeError(f"Dynamic text tag was not retained: {dynamic_text}")

        indicator_removed = server._run_live(
            "remove_extent_indicators",
            {
                "layout_name": layout_name,
                "map_frame_name": "Overview Frame",
                "indicator_names": ["Validation Extent Updated"],
                "confirm_remove": True,
            },
        )
        clipping_disabled = server._run_live(
            "configure_map_clipping",
            {
                "map_name": detail_map,
                "mode": "None",
                "border_visible": False,
                "clear_custom_shape": True,
                "clear_exclusions": True,
            },
        )
        if clipping_disabled.get("clipping_mode") != "None":
            raise RuntimeError(f"Clipping did not disable: {clipping_disabled}")

        summary["map_clipping"] = {
            "enabled": clipping_enabled,
            "disabled": clipping_disabled,
        }
        summary["extent_indicator"] = {
            "added": indicator_added,
            "configured": indicator_configured,
            "inventory": indicator_inventory,
            "removed": indicator_removed,
        }
        summary["dynamic_text"] = dynamic_text
    finally:
        cleanup: dict[str, object] = {}
        if layout_created:
            try:
                cleanup["layout"] = server._run_live(
                    "remove_layout",
                    {"layout_name": layout_name, "confirm_remove": True},
                )
            except Exception as exc:
                cleanup["layout_error"] = str(exc)
        cleanup_maps: list[object] = []
        for map_name in reversed(created_maps):
            try:
                cleanup_maps.append(
                    server._run_live(
                        "remove_map", {"map_name": map_name, "confirm_remove": True}
                    )
                )
            except Exception as exc:
                cleanup_maps.append({"map": map_name, "error": str(exc)})
        cleanup["maps"] = cleanup_maps
        summary["cleanup"] = cleanup

    output = ROOT / "outputs" / "bridge_runtime_tests" / "live_v012_validation_latest.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(
        json.dumps(summary, ensure_ascii=False, indent=2, default=str),
        encoding="utf-8",
    )
    print(json.dumps(summary, ensure_ascii=False, indent=2, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
