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
    if status.get("protocol_version") not in {"0.19.0", "0.20.0", "0.21.0", "0.22.0", "0.23.0", "0.23.1", "0.23.2", "0.24.0", "0.25.0", "0.26.0", "0.27.0", "0.27.1", "0.27.2", "0.27.3", "0.28.0"}:
        raise RuntimeError(f"Expected protocol 0.19.0 or a compatible later version, got {status.get('protocol_version')}")
    actions = set(status.get("capabilities") or [])
    required = {
        "get_map_view_state",
        "configure_map_view",
        "list_elevation_surfaces",
        "configure_elevation_surface",
        "configure_layer_elevation",
        "sample_elevation_surface",
        "get_elevation_profile",
        "get_animation_state",
        "control_animation",
        "begin_animation_export",
    }
    if len(actions) < 184 or not required.issubset(actions):
        raise RuntimeError(f"Expected at least 184 actions including v0.19 additions; got {len(actions)}")

    vector_root = ROOT / "outputs" / "bridge_runtime_tests" / "vector_inputs_v015"
    manifest = json.loads((vector_root / "manifest.json").read_text(encoding="utf-8"))
    residents = Path(manifest["residents"])
    source_before = fingerprint_tree(vector_root)
    project_path = Path(str(status["project"]))
    project_before = project_path.stat()

    marker = str(int(time.time()))
    map_name = f"CodexV019Scene_{marker}"
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
            {
                "map_name": map_name,
                "map_type": "Scene",
                "viewing_mode": "SceneLocal",
                "basemap": "None",
                "open_map": True,
            },
        )
        created = True
        for _ in range(30):
            active = server._run_live("status", {}).get("active_map")
            if active == map_name:
                break
            time.sleep(0.25)
        else:
            raise RuntimeError(f"Temporary scene did not become active: {active}")

        server._run_live("add_data", {"map_name": map_name, "data_paths": [str(residents)]})
        original_view = server._run_live("get_map_view_state", {"map_name": map_name})
        styled_view = server._run_live(
            "configure_map_view",
            {
                "map_name": map_name,
                "field_of_view": 55,
                "scene_drawing_mode": "Perspective",
                "visual_effect_preset": "black_and_white",
                "postprocessing_presets": ["bloom"],
                "effect_parameters": {"strength": 0.2, "threshold": 0.8},
            },
        )
        if (styled_view.get("visual_effect") or {}).get("cim_type") != "CIMBlackAndWhiteEffect":
            raise RuntimeError(f"Visual effect did not apply: {styled_view}")
        restored_view = server._run_live(
            "configure_map_view",
            {
                "map_name": map_name,
                "field_of_view": original_view.get("field_of_view", 55),
                "clear_visual_effect": True,
                "clear_camera_effect": True,
                "postprocessing_effects": [],
            },
        )

        surfaces = server._run_live(
            "list_elevation_surfaces", {"map_name": map_name, "include_cim": True}
        )
        if not surfaces.get("surfaces"):
            raise RuntimeError(f"Temporary scene exposed no elevation surface: {surfaces}")
        ground = next(
            (item for item in surfaces["surfaces"] if item.get("is_ground")),
            surfaces["surfaces"][0],
        )
        original_exaggeration = ground.get("vertical_exaggeration", 1)
        configured_surface = server._run_live(
            "configure_elevation_surface",
            {
                "map_name": map_name,
                "surface_name": ground["name"],
                "vertical_exaggeration": 1.25,
                "enable_surface_shading": True,
            },
        )
        restored_surface = server._run_live(
            "configure_elevation_surface",
            {
                "map_name": map_name,
                "surface_name": ground["name"],
                "vertical_exaggeration": original_exaggeration,
            },
        )

        original_elevation = server._run_live(
            "configure_layer_elevation",
            {"map_name": map_name, "layer_name": "validation_residents", "operation": "inspect"},
        )
        configured_elevation = server._run_live(
            "configure_layer_elevation",
            {
                "map_name": map_name,
                "layer_name": "validation_residents",
                "operation": "set",
                "elevation_type": "OnGround",
            },
        )
        restored_elevation = server._run_live(
            "configure_layer_elevation",
            {
                "map_name": map_name,
                "layer_name": "validation_residents",
                "operation": "set",
                "elevation_type": original_elevation.get("elevation_type", "None"),
            },
        )

        samples = server._run_live(
            "sample_elevation_surface",
            {
                "map_name": map_name,
                "surface_name": ground["name"],
                "points": [{"x": 0, "y": 0}, {"x": 1, "y": 1}],
            },
        )
        profile = server._run_live(
            "get_elevation_profile",
            {
                "map_name": map_name,
                "surface_name": ground["name"],
                "start": {"x": 0, "y": 0},
                "end": {"x": 1, "y": 1},
                "num_points": 8,
            },
        )
        animation = server._run_live("get_animation_state", {"map_name": map_name})
        animation_stopped = server._run_live(
            "control_animation", {"map_name": map_name, "operation": "stop"}
        )

        summary["map_view"] = {
            "original": original_view,
            "styled": styled_view,
            "restored": restored_view,
        }
        summary["elevation_surface"] = {
            "inventory": surfaces,
            "configured": configured_surface,
            "restored": restored_surface,
        }
        summary["layer_elevation"] = {
            "original": original_elevation,
            "configured": configured_elevation,
            "restored": restored_elevation,
        }
        summary["samples"] = samples
        summary["profile"] = profile
        summary["animation"] = {"state": animation, "stopped": animation_stopped}
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
    summary["synthetic_source_fingerprint_unchanged"] = (
        source_before == fingerprint_tree(vector_root)
    )
    if not summary["project_file_unchanged"]:
        raise RuntimeError("The open .aprx file changed during v0.19 validation")
    if not summary["synthetic_source_fingerprint_unchanged"]:
        raise RuntimeError("Synthetic validation inputs changed during v0.19 validation")

    output = ROOT / "outputs" / "bridge_runtime_tests" / "live_v019_validation_latest.json"
    output.write_text(json.dumps(summary, ensure_ascii=False, indent=2, default=str), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
