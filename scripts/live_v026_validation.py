from __future__ import annotations

import hashlib
import json
import sys
import time
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import ArcGISMCP


def fingerprint_tree(root: Path) -> dict[str, str]:
    return {
        path.relative_to(root).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in sorted(item for item in root.rglob("*") if item.is_file())
    }


def main() -> int:
    server = ArcGISMCP(ROOT / "config.json")
    server.live_timeout = min(server.live_timeout, 120)
    status = server._run_live("status", {})
    if status.get("protocol_version") not in {"0.26.0", "0.27.0", "0.27.1", "0.27.2", "0.27.3", "0.28.0", "0.28.1", "0.28.2"}:
        raise RuntimeError(f"Expected protocol 0.26.0 or compatible 0.27.0, got {status.get('protocol_version')}")
    actions = set(status.get("capabilities") or [])
    required = {
        "get_portal_state", "inspect_time_zones", "inspect_locators",
        "geocode_location", "get_operation_history", "control_operation_history",
        "geometry_engine",
    }
    if len(actions) < 228 or not required.issubset(actions):
        raise RuntimeError(f"Expected at least 228 actions including v0.26 additions; got {len(actions)}")

    vector_root = ROOT / "outputs" / "bridge_runtime_tests" / "vector_inputs_v015"
    manifest = json.loads((vector_root / "manifest.json").read_text(encoding="utf-8"))
    residents = Path(manifest["residents"])
    source_before = fingerprint_tree(vector_root)
    project_path = Path(str(status["project"]))
    project_before = project_path.stat()

    marker = str(int(time.time()))
    map_name = f"CodexV026_{marker}"
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
        portal = server._run_live("get_portal_state", {})
        if portal.get("tokens_returned") is not False or portal.get("credentials_returned") is not False:
            raise RuntimeError(f"Portal inspection safety metadata is invalid: {portal}")
        time_zones = server._run_live(
            "inspect_time_zones",
            {"query": "Shanghai", "time_zone_id": "Asia/Shanghai", "max_results": 20},
        )
        if not time_zones.get("time_zones"):
            raise RuntimeError(f"Time-zone search returned no Shanghai entry: {time_zones}")

        point = {"x": 111.3, "y": 30.7, "spatialReference": {"wkid": 4326}}
        geometry_description = server._run_live(
            "geometry_engine", {"operation": "describe", "geometry": point}
        )
        geometry_buffer = server._run_live(
            "geometry_engine",
            {"operation": "geodesic_buffer", "geometry": point, "distance": 1000.0},
        )
        if geometry_description.get("project_modified") is not False:
            raise RuntimeError(f"GeometryEngine changed project state: {geometry_description}")
        if not (geometry_buffer.get("result") or {}).get("esri_json"):
            raise RuntimeError(f"GeometryEngine buffer returned no geometry: {geometry_buffer}")

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
            raise RuntimeError("Temporary v0.26 map did not become active")
        server._run_live("add_data", {"map_name": map_name, "data_paths": [str(residents)]})
        locators = server._run_live("inspect_locators", {"map_name": map_name})
        history = server._run_live(
            "get_operation_history",
            {"scope": "map", "item_name": map_name, "max_operations": 50},
        )
        if locators.get("source_data_modified") is not False:
            raise RuntimeError(f"Locator inventory did not affirm read-only behavior: {locators}")
        if history.get("source_data_modified") is not False:
            raise RuntimeError(f"Operation history did not affirm read-only behavior: {history}")

        summary.update(
            {
                "portal": portal,
                "time_zones": time_zones,
                "geometry": {"describe": geometry_description, "buffer": geometry_buffer},
                "locators": locators,
                "operation_history": history,
                "geocode_executed": False,
                "operation_history_mutated": False,
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
    summary["synthetic_source_fingerprint_unchanged"] = source_before == fingerprint_tree(vector_root)
    if not summary["project_file_unchanged"]:
        raise RuntimeError("The open .aprx file changed during v0.26 validation")
    if not summary["synthetic_source_fingerprint_unchanged"]:
        raise RuntimeError("Synthetic source data changed during v0.26 validation")

    output = ROOT / "outputs" / "bridge_runtime_tests" / "live_v026_validation_latest.json"
    output.write_text(json.dumps(summary, ensure_ascii=False, indent=2, default=str), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
