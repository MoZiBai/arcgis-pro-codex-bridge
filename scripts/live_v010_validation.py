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
    server.live_timeout = min(server.live_timeout, 90)
    status = server._run_live("status", {})
    if status.get("protocol_version") != "0.10.7":
        raise RuntimeError(
            f"Expected protocol 0.10.7, got {status.get('protocol_version')}"
        )
    capabilities = server._run_live("capabilities", {})
    actions = set(capabilities.get("actions") or [])
    required = {
        "get_data_connection",
        "set_data_connection",
        "replace_data_source",
        "spatial_query_features",
        "spatial_select_features",
    }
    if len(actions) < 125 or not required.issubset(actions):
        raise RuntimeError(
            f"Expected at least 125 actions including v0.10 additions; got {len(actions)}"
        )

    marker = str(int(time.time()))
    map_name = f"CodexV010Validation_{marker}"
    synthetic_gdb = (
        ROOT
        / "outputs"
        / "宜昌市测试数据"
        / "yichang_synthetic_20260730_230117.gdb"
    )
    district_gdb = synthetic_gdb / "模拟区县"
    roads_gdb = synthetic_gdb / "模拟道路"
    residents_gdb = synthetic_gdb / "模拟居民点"
    district_shp = (
        ROOT
        / "outputs"
        / "宜昌市测试数据"
        / "shapefiles_20260730_230549"
        / "模拟区县.shp"
    )
    for path in (synthetic_gdb, district_shp):
        if not path.exists():
            raise FileNotFoundError(f"Synthetic validation input is missing: {path}")

    summary: dict[str, object] = {
        "handshake": {
            "addin_version": status.get("addin_version"),
            "protocol_version": status.get("protocol_version"),
            "action_count": len(actions),
        },
        "source_data_modified": False,
        "project_saved": False,
    }
    active_map = status.get("active_map")
    if active_map:
        camera = server._run_live("get_camera", {"map_name": active_map})
        if camera.get("scale") is None or camera.get("x") is None or camera.get("y") is None:
            raise RuntimeError("Active-map camera did not return finite scale and center coordinates")
        summary["camera"] = {
            "map": camera.get("map"),
            "x": camera.get("x"),
            "y": camera.get("y"),
            "z": camera.get("z"),
            "scale": camera.get("scale"),
            "heading": camera.get("heading"),
            "spatial_reference": camera.get("spatial_reference"),
        }
    map_created = False
    try:
        summary["create_map"] = server._run_live(
            "create_map", {"map_name": map_name, "open_map": False}
        )
        map_created = True
        summary["add_synthetic_data"] = server._run_live(
            "add_data",
            {
                "map_name": map_name,
                "data_paths": [
                    str(district_gdb),
                    str(roads_gdb),
                    str(residents_gdb),
                ],
            },
        )

        district_connection = server._run_live(
            "get_data_connection",
            {"map_name": map_name, "map_member_name": "模拟区县"},
        )
        roads_connection = server._run_live(
            "get_data_connection",
            {"map_name": map_name, "map_member_name": "模拟道路"},
        )
        if not roads_connection.get("cim_json"):
            raise RuntimeError("Synthetic road layer returned no CIM data connection")
        connection_type = str(roads_connection.get("connection_type") or "").lower()
        normalized_type = (
            "feature_dataset" if "featuredataset" in connection_type else "standard"
        )
        connection_roundtrip = server._run_live(
            "set_data_connection",
            {
                "map_name": map_name,
                "map_member_name": "模拟道路",
                "connection_type": normalized_type,
                "cim_json": roads_connection["cim_json"],
                "validate_connection": True,
                "confirm_replace_connection": True,
            },
        )
        summary["data_connection"] = {
            "district": district_connection,
            "roads": {
                "connection_status": roads_connection.get("connection_status"),
                "connection_type": roads_connection.get("connection_type"),
                "has_cim_json": bool(roads_connection.get("cim_json")),
            },
            "roundtrip": connection_roundtrip,
        }

        replaced = server._run_live(
            "replace_data_source",
            {
                "map_name": map_name,
                "map_member_name": "模拟区县",
                "data_path": str(district_shp),
                "confirm_replace_connection": True,
            },
        )
        replacement_connection = server._run_live(
            "get_data_connection",
            {"map_name": map_name, "map_member_name": "模拟区县"},
        )
        restored = server._run_live(
            "replace_data_source",
            {
                "map_name": map_name,
                "map_member_name": "模拟区县",
                "data_path": str(district_gdb),
                "confirm_replace_connection": True,
            },
        )
        restored_connection = server._run_live(
            "get_data_connection",
            {"map_name": map_name, "map_member_name": "模拟区县"},
        )
        summary["replace_data_source"] = {
            "to_shapefile": replaced,
            "replacement_status": replacement_connection.get("connection_status"),
            "restore_to_gdb": restored,
            "restored_status": restored_connection.get("connection_status"),
        }

        inline_query = server._run_live(
            "spatial_query_features",
            {
                "map_name": map_name,
                "target_layer_name": "模拟居民点",
                "geometry": {
                    "type": "envelope",
                    "bounds": {
                        "xmin": 110.0,
                        "ymin": 29.8,
                        "xmax": 112.3,
                        "ymax": 31.8,
                    },
                },
                "spatial_relationship": "Intersects",
                "fields": ["NAME", "TYPE", "POP"],
                "max_rows": 20,
                "include_geometry": True,
            },
        )
        source_layer_query = server._run_live(
            "spatial_query_features",
            {
                "map_name": map_name,
                "target_layer_name": "模拟居民点",
                "source_layer_name": "模拟区县",
                "source_where_clause": "1=1",
                "max_source_features": 100,
                "spatial_relationship": "Intersects",
                "fields": ["NAME", "TYPE", "POP"],
                "max_rows": 50,
            },
        )
        selected = server._run_live(
            "spatial_select_features",
            {
                "map_name": map_name,
                "target_layer_name": "模拟居民点",
                "source_layer_name": "模拟区县",
                "source_where_clause": "1=1",
                "max_source_features": 100,
                "spatial_relationship": "Intersects",
                "combination": "New",
                "max_ids": 1000,
            },
        )
        selection = server._run_live(
            "get_selection",
            {
                "map_name": map_name,
                "layer_names": ["模拟居民点"],
                "max_ids": 1000,
            },
        )
        cleared = server._run_live(
            "clear_selection",
            {"map_name": map_name, "layer_names": ["模拟居民点"]},
        )
        if inline_query.get("total_matching") != 120:
            raise RuntimeError(
                f"Inline spatial count mismatch: {inline_query.get('total_matching')} != 120"
            )
        if inline_query.get("returned") != 20:
            raise RuntimeError(
                f"Inline spatial row limit mismatch: {inline_query.get('returned')} != 20"
            )
        if any(
            int(row.get("__object_id", -1)) < 0 or row.get("__geometry") is None
            for row in inline_query.get("rows", [])
        ):
            raise RuntimeError("Inline spatial rows did not include valid OIDs and geometry")
        if source_layer_query.get("total_matching") != 120:
            raise RuntimeError(
                "Source-layer spatial count mismatch: "
                f"{source_layer_query.get('total_matching')} != 120"
            )
        if selected.get("selected_count") != 120:
            raise RuntimeError(
                f"Spatial selection count mismatch: {selected.get('selected_count')} != 120"
            )
        selected_layers = selection.get("layers") or []
        if not selected_layers or selected_layers[0].get("selected_count") != 120:
            raise RuntimeError("Selection inventory did not round-trip 120 selected residents")
        summary["spatial"] = {
            "inline_query": inline_query,
            "source_layer_query": source_layer_query,
            "select": selected,
            "selection_inventory": selection,
            "clear": cleared,
        }
    finally:
        if map_created:
            try:
                summary["cleanup"] = server._run_live(
                    "remove_map",
                    {"map_name": map_name, "confirm_remove": True},
                )
            except Exception as exc:
                summary["cleanup_error"] = str(exc)

    print(json.dumps(summary, ensure_ascii=False, indent=2, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
