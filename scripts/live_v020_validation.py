from __future__ import annotations

import json
import sys
import time
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import ArcGISMCP


def expect_live_error(server: ArcGISMCP, action: str, payload: dict[str, object], text: str) -> str:
    try:
        server._run_live(action, payload)
    except Exception as exc:
        if text.casefold() not in str(exc).casefold():
            raise RuntimeError(f"Unexpected {action} error: {exc}") from exc
        return str(exc)
    raise RuntimeError(f"{action} unexpectedly accepted an incompatible layer")


def main() -> int:
    server = ArcGISMCP(ROOT / "config.json")
    server.live_timeout = min(server.live_timeout, 120)
    status = server._run_live("status", {})
    if status.get("protocol_version") not in {"0.20.0", "0.21.0", "0.22.0", "0.23.0", "0.23.1", "0.23.2", "0.24.0", "0.25.0", "0.26.0", "0.27.0", "0.27.1", "0.27.2", "0.27.3", "0.28.0", "0.28.1"}:
        raise RuntimeError(f"Expected protocol 0.20.0 or a compatible later version, got {status.get('protocol_version')}")
    actions = set(status.get("capabilities") or [])
    required = {
        "get_view_filter_state",
        "configure_view_filters",
        "list_map_graphics",
        "manage_map_graphics",
        "get_map_graphic_cim",
        "set_map_graphic_cim",
        "inspect_las_layer",
        "configure_las_layer",
        "inspect_voxel_layer",
        "configure_voxel_layer",
    }
    if len(actions) < 194 or not required.issubset(actions):
        raise RuntimeError(f"Expected at least 194 actions including v0.20 additions; got {len(actions)}")

    project_path = Path(str(status["project"]))
    project_before = project_path.stat()
    marker = str(int(time.time()))
    map_name = f"CodexV020_{marker}"
    graphics_layer = "V020 Graphics"
    point_name = "V020 Point"
    line_name = "V020 Line"
    created = False
    summary: dict[str, object] = {
        "handshake": {
            "addin_version": status.get("addin_version"),
            "protocol_version": status.get("protocol_version"),
            "action_count": len(actions),
        },
        "source_data_modified": False,
        "project_saved": False,
    }
    try:
        server._run_live(
            "create_map",
            {"map_name": map_name, "map_type": "Map", "basemap": "None", "open_map": True},
        )
        created = True
        for _ in range(40):
            if server._run_live("status", {}).get("active_map") == map_name:
                break
            time.sleep(0.25)
        else:
            raise RuntimeError("Temporary v0.20 map did not become active")

        filters_before = server._run_live("get_view_filter_state", {"map_name": map_name})
        filters_configured = server._run_live(
            "configure_view_filters",
            {
                "map_name": map_name,
                "time": {
                    "start": "2020-01-01T00:00:00Z",
                    "end": "2020-01-02T00:00:00Z",
                },
                "range": {"minimum": 0, "maximum": 10},
                "floor_filter": {"enabled": False, "minimized": True},
            },
        )
        if filters_configured.get("time") is None:
            raise RuntimeError(f"View filters did not apply: {filters_configured}")
        # A blank validation map has no range-enabled layer, so Pro correctly
        # reports no active range after accepting the requested state.
        if filters_configured.get("range_names") and filters_configured.get("range") is None:
            raise RuntimeError(f"View range did not apply on a range-enabled map: {filters_configured}")

        drawn = server._run_live(
            "draw_graphics",
            {
                "map_name": map_name,
                "graphics_layer_name": graphics_layer,
                "graphics": [
                    {"type": "point", "name": point_name, "coordinates": [0, 0]},
                    {
                        "type": "line",
                        "name": line_name,
                        "coordinates": [[0, 0], [10, 10]],
                    },
                ],
            },
        )
        listed = server._run_live(
            "list_map_graphics",
            {"map_name": map_name, "graphics_layer_name": graphics_layer, "include_cim": True},
        )
        elements = listed["graphics_layers"][0]["elements"]
        if {item["name"] for item in elements} != {point_name, line_name}:
            raise RuntimeError(f"Map graphics inventory mismatch: {listed}")

        updated = server._run_live(
            "manage_map_graphics",
            {
                "map_name": map_name,
                "graphics_layer_name": graphics_layer,
                "operation": "update",
                "element_name": point_name,
                "visible": False,
                "rotation": 15,
            },
        )
        graphic_cim = server._run_live(
            "get_map_graphic_cim",
            {
                "map_name": map_name,
                "graphics_layer_name": graphics_layer,
                "element_name": point_name,
            },
        )
        same_type_roundtrip = server._run_live(
            "set_map_graphic_cim",
            {
                "map_name": map_name,
                "graphics_layer_name": graphics_layer,
                "element_name": point_name,
                "cim_json": graphic_cim["cim_json"],
                "confirm_replace_definition": True,
            },
        )
        patch_dry_run = server._patch_cim_object(
            {
                "target_type": "map_graphic",
                "map_name": map_name,
                "graphics_layer_name": graphics_layer,
                "element_name": point_name,
                "patch": {"visible": True},
                "expected_cim_type": graphic_cim["cim_type"],
                "dry_run": True,
            }
        )

        las_type_guard = expect_live_error(
            server,
            "inspect_las_layer",
            {"map_name": map_name, "layer_name": graphics_layer},
            "las dataset layer not found",
        )
        voxel_type_guard = expect_live_error(
            server,
            "inspect_voxel_layer",
            {"map_name": map_name, "layer_name": graphics_layer},
            "voxel layer not found",
        )
        removed = server._run_live(
            "manage_map_graphics",
            {
                "map_name": map_name,
                "graphics_layer_name": graphics_layer,
                "operation": "remove",
                "element_names": [point_name, line_name],
                "confirm_remove": True,
            },
        )

        summary.update(
            {
                "view_filters": {
                    "before": filters_before,
                    "configured": filters_configured,
                },
                "graphics": {
                    "drawn": drawn,
                    "listed": listed,
                    "updated": updated,
                    "same_type_roundtrip": same_type_roundtrip,
                    "patch_dry_run": patch_dry_run,
                    "removed": removed,
                },
                "specialized_layer_type_guards": {
                    "las": las_type_guard,
                    "voxel": voxel_type_guard,
                },
            }
        )
    finally:
        if created:
            try:
                cleanup = server._run_live(
                    "remove_map", {"map_name": map_name, "confirm_remove": True}
                )
            except Exception as exc:
                cleanup = {"map": map_name, "error": str(exc)}
        else:
            cleanup = {"map": map_name, "created": False}
        summary["cleanup"] = {"maps": [cleanup]}

    project_after = project_path.stat()
    summary["project_file_unchanged"] = (
        project_before.st_size == project_after.st_size
        and project_before.st_mtime_ns == project_after.st_mtime_ns
    )
    if not summary["project_file_unchanged"]:
        raise RuntimeError("The open .aprx file changed during v0.20 validation")

    output = ROOT / "outputs" / "bridge_runtime_tests" / "live_v020_validation_latest.json"
    output.write_text(json.dumps(summary, ensure_ascii=False, indent=2, default=str), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
