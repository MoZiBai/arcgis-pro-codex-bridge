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
    if status.get("protocol_version") not in {"0.24.0", "0.25.0", "0.26.0", "0.27.0", "0.27.1", "0.27.2", "0.27.3", "0.28.0"}:
        raise RuntimeError(f"Expected protocol 0.24.0 or a compatible later version, got {status.get('protocol_version')}")
    actions = set(status.get("capabilities") or [])
    required = {"transform_view_coordinates", "manage_layout_guides"}
    if len(actions) < 221 or not required.issubset(actions):
        raise RuntimeError(f"Expected at least 221 actions including v0.24 additions; got {len(actions)}")

    project_path = Path(str(status["project"]))
    project_before = project_path.stat()
    output_root = ROOT / "outputs" / "bridge_runtime_tests"
    output_root.mkdir(parents=True, exist_ok=True)
    eps_output = output_root / "live_v024_layout.eps"
    aix_output = output_root / "live_v024_layout.aix"
    for path in (eps_output, aix_output):
        path.unlink(missing_ok=True)

    marker = str(int(time.time()))
    map_name = f"CodexV024Map_{marker}"
    layout_name = f"CodexV024Layout_{marker}"
    map_created = False
    layout_created = False
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
        source_map = str(status.get("active_map") or (status.get("maps") or [""])[0])
        if not source_map:
            raise RuntimeError("No existing map is available for coordinate-transform validation")
        server._run_live("open_map", {"map_name": source_map})
        for _ in range(40):
            if server._run_live("status", {}).get("active_map") == source_map:
                break
            time.sleep(0.25)
        camera = server._run_live("get_camera", {"map_name": source_map})
        if camera.get("x") is None or camera.get("y") is None:
            server._run_live("zoom_full_extent", {"map_name": source_map})
            camera = server._run_live("get_camera", {"map_name": source_map})
        if camera.get("x") is None or camera.get("y") is None:
            raise RuntimeError(f"Existing map camera is not finite: {camera}")
        map_to_client = server._run_live(
            "transform_view_coordinates",
            {
                "map_name": source_map,
                "operation": "map_to_client",
                "x": camera["x"],
                "y": camera["y"],
            },
        )
        client_result = map_to_client.get("result") or {}
        if client_result.get("x") is None or client_result.get("y") is None:
            raise RuntimeError(f"Map-to-client conversion returned non-finite coordinates: {map_to_client}")
        client_to_map = server._run_live(
            "transform_view_coordinates",
            {
                "map_name": source_map,
                "operation": "client_to_map",
                "x": client_result["x"],
                "y": client_result["y"],
            },
        )
        map_result = client_to_map.get("result") or {}
        if map_result.get("x") is None or map_result.get("y") is None:
            raise RuntimeError(f"Client-to-map conversion returned non-finite coordinates: {client_to_map}")
        client_to_screen = server._run_live(
            "transform_view_coordinates",
            {"map_name": source_map, "operation": "client_to_screen", "x": 10.0, "y": 20.0},
        )

        server._run_live(
            "create_map",
            {"map_name": map_name, "map_type": "Map", "basemap": "None", "open_map": True},
        )
        map_created = True
        for _ in range(40):
            if server._run_live("status", {}).get("active_map") == map_name:
                break
            time.sleep(0.25)
        else:
            raise RuntimeError("Temporary v0.24 map did not become active")
        server._run_live(
            "create_layout",
            {
                "layout_name": layout_name,
                "map_name": map_name,
                "map_frame_name": "Validation Frame",
                "legend": False,
                "north_arrow": False,
                "scale_bar": False,
                "open_layout": False,
            },
        )
        layout_created = True
        guide_before = server._run_live(
            "manage_layout_guides", {"layout_name": layout_name, "operation": "inspect"}
        )
        guide_added = server._run_live(
            "manage_layout_guides",
            {
                "layout_name": layout_name,
                "operation": "add",
                "guides": [
                    {"orientation": "Vertical", "position": 1.0},
                    {"orientation": "Horizontal", "position": 1.5},
                ],
            },
        )
        if len(guide_added.get("guides") or []) != len(guide_before.get("guides") or []) + 2:
            raise RuntimeError(f"Layout guides were not added: {guide_added}")
        guide_removed = server._run_live(
            "manage_layout_guides",
            {"layout_name": layout_name, "operation": "remove", "guide_indices": [0]},
        )
        guide_cleared = server._run_live(
            "manage_layout_guides",
            {"layout_name": layout_name, "operation": "clear", "confirm_clear": True},
        )
        if guide_cleared.get("guides"):
            raise RuntimeError(f"Layout guides were not cleared: {guide_cleared}")

        eps_result = server.call_tool(
            "live_export_layout",
            {"layout_name": layout_name, "output_file": str(eps_output), "dpi": 96},
        )
        aix_result = server.call_tool(
            "live_export_layout",
            {"layout_name": layout_name, "output_file": str(aix_output), "dpi": 96},
        )
        if not eps_output.exists() or eps_output.stat().st_size == 0:
            raise RuntimeError(f"EPS export was not created: {eps_result}")
        if not aix_output.exists() or aix_output.stat().st_size == 0:
            raise RuntimeError(f"AIX export was not created: {aix_result}")

        summary.update(
            {
                "coordinate_transforms": {
                    "map_to_client": map_to_client,
                    "client_to_map": client_to_map,
                    "client_to_screen": client_to_screen,
                },
                "layout_guides": {
                    "before": guide_before,
                    "added": guide_added,
                    "removed": guide_removed,
                    "cleared": guide_cleared,
                },
                "exports": {
                    "eps": {"result": eps_result, "size": eps_output.stat().st_size},
                    "aix": {"result": aix_result, "size": aix_output.stat().st_size},
                },
            }
        )
    finally:
        cleanup: dict[str, object] = {"layouts": [], "maps": []}
        if layout_created:
            try:
                cleanup["layouts"].append(
                    server._run_live("remove_layout", {"layout_name": layout_name, "confirm_remove": True})
                )
            except Exception as exc:
                cleanup["layout_error"] = str(exc)
        if map_created:
            try:
                cleanup["maps"].append(
                    server._run_live("remove_map", {"map_name": map_name, "confirm_remove": True})
                )
            except Exception as exc:
                cleanup["map_error"] = str(exc)
        summary["cleanup"] = cleanup

    project_after = project_path.stat()
    summary["project_file_unchanged"] = (
        project_before.st_size == project_after.st_size
        and project_before.st_mtime_ns == project_after.st_mtime_ns
    )
    if not summary["project_file_unchanged"]:
        raise RuntimeError("The open .aprx file changed during v0.24 validation")

    output = output_root / "live_v024_validation_latest.json"
    output.write_text(json.dumps(summary, ensure_ascii=False, indent=2, default=str), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
