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


def expect_live_error(server: ArcGISMCP, action: str, payload: dict[str, object], text: str) -> str:
    try:
        server._run_live(action, payload)
    except Exception as exc:
        if text.casefold() not in str(exc).casefold():
            raise RuntimeError(f"Unexpected {action} error: {exc}") from exc
        return str(exc)
    raise RuntimeError(f"{action} unexpectedly accepted an unsafe/incompatible payload")


def main() -> int:
    server = ArcGISMCP(ROOT / "config.json")
    server.live_timeout = min(server.live_timeout, 120)
    status = server._run_live("status", {})
    if status.get("protocol_version") not in {"0.17.0", "0.18.0", "0.19.0", "0.20.0", "0.21.0", "0.22.0", "0.23.0", "0.23.1", "0.23.2", "0.24.0", "0.25.0", "0.26.0", "0.27.0", "0.27.1", "0.27.2", "0.27.3", "0.28.0", "0.28.1", "0.28.2", "0.29.0"}:
        raise RuntimeError(f"Expected protocol 0.17.0 or a compatible newer bridge, got {status.get('protocol_version')}")
    actions = set(status.get("capabilities") or [])
    required = {
        "configure_visual_variables",
        "configure_raster_rendering_rule",
        "configure_multidimensional_raster",
        "get_layer_cim",
        "set_layer_cim",
    }
    if len(actions) < 159 or not required.issubset(actions):
        raise RuntimeError(
            f"Expected 159 actions including v0.17 additions; got {len(actions)}"
        )

    vector_root = ROOT / "outputs" / "bridge_runtime_tests" / "vector_inputs_v015"
    raster_root = ROOT / "outputs" / "bridge_runtime_tests" / "raster_inputs_v016"
    vector_manifest = json.loads((vector_root / "manifest.json").read_text(encoding="utf-8"))
    raster_manifest = json.loads((raster_root / "manifest.json").read_text(encoding="utf-8"))
    residents = Path(vector_manifest["residents"])
    elevation = Path(raster_manifest["elevation"])

    marker = str(int(time.time()))
    map_name = f"CodexV017Advanced_{marker}"
    project_path = Path(str(status["project"]))
    project_before = project_path.stat()
    vector_before = fingerprint_tree(vector_root)
    raster_before = fingerprint_tree(raster_root)
    summary: dict[str, object] = {
        "handshake": {
            "addin_version": status.get("addin_version"),
            "protocol_version": status.get("protocol_version"),
            "action_count": len(actions),
        },
        "source_data_modified": False,
        "project_saved": False,
    }
    map_created = False
    try:
        server._run_live("create_map", {"map_name": map_name, "open_map": False})
        map_created = True
        server._run_live(
            "add_data",
            {"map_name": map_name, "data_paths": [str(residents), str(elevation)]},
        )
        server._run_live(
            "apply_vector_symbology",
            {
                "map_name": map_name,
                "layer_name": "validation_residents",
                "renderer": "simple",
                "symbol": {"color": "#2A9D8FCC", "size": 7, "marker": "Circle"},
            },
        )
        visual_variables = server._run_live(
            "configure_visual_variables",
            {
                "map_name": map_name,
                "layer_name": "validation_residents",
                "operation": "replace",
                "variables": [
                    {"type": "size", "field": "POP", "min_value": 500, "max_value": 95500, "min_size": 3, "max_size": 18},
                    {"type": "color", "field": "POP", "min_value": 500, "max_value": 95500, "color_stops": ["#E9F5DB", "#90A955", "#31572C"]},
                    {"type": "transparency", "field": "POP", "data_values": [500, 95500], "transparency_values": [65, 5]},
                    {"type": "rotation", "expression": "$feature.POP % 360", "rotation_type": "geographic"},
                ],
            },
        )
        variable_types = {item.get("type") for item in visual_variables.get("visual_variables", [])}
        if variable_types != {"size", "color", "transparency", "rotation"}:
            raise RuntimeError(f"Visual variables did not round-trip: {visual_variables}")

        layer_cim = server._run_live(
            "get_layer_cim",
            {"map_name": map_name, "layer_name": "validation_residents"},
        )
        cim_json = str(layer_cim["cim_json"])
        for class_name in (
            "CIMSizeVisualVariable",
            "CIMColorVisualVariable",
            "CIMTransparencyVisualVariable",
            "CIMRotationVisualVariable",
        ):
            if class_name not in cim_json:
                raise RuntimeError(f"Layer CIM is missing {class_name}")
        layer_round_trip = server._run_live(
            "set_layer_cim",
            {
                "map_name": map_name,
                "layer_name": "validation_residents",
                "cim_json": cim_json,
                "confirm_replace_definition": True,
            },
        )
        if layer_round_trip.get("data_connection_preserved") is not True:
            raise RuntimeError(f"Layer source was not preserved: {layer_round_trip}")

        rendering_rule_before = server._run_live(
            "configure_raster_rendering_rule",
            {
                "map_name": map_name,
                "layer_name": "v016_elevation.tif",
                "operation": "inspect",
            },
        )
        rendering_rule_guard = expect_live_error(
            server,
            "configure_raster_rendering_rule",
            {
                "map_name": map_name,
                "layer_name": "v016_elevation.tif",
                "operation": "set",
                "confirm_replace_definition": True,
            },
            "cim_json",
        )

        multidimensional_guard = expect_live_error(
            server,
            "configure_multidimensional_raster",
            {
                "map_name": map_name,
                "layer_name": "v016_elevation.tif",
                "operation": "set",
                "variable_name": "validation_variable",
                "range_dimension_name": "depth",
                "range_min": 10,
                "range_max": 10,
                "time_start": "2026-01-01T00:00:00Z",
                "time_end": "2026-01-01T00:00:00Z",
                "additional_dimensions": [{"name": "scenario", "min": 1, "max": 1}],
            },
            "not multidimensional",
        )

        summary["visual_variables"] = visual_variables
        summary["layer_cim_round_trip"] = {
            "cim_type": layer_round_trip.get("cim_type"),
            "data_connection_preserved": layer_round_trip.get("data_connection_preserved"),
            "project_structure_preserved": layer_round_trip.get("project_structure_preserved"),
        }
        summary["rendering_rule"] = {
            "inspected": rendering_rule_before,
            "unsafe_ad_hoc_set_rejected": rendering_rule_guard,
        }
        summary["multidimensional"] = {
            "ordinary_raster_set_rejected": multidimensional_guard,
        }
    finally:
        if map_created:
            try:
                cleanup_maps = [server._run_live("remove_map", {"map_name": map_name, "confirm_remove": True})]
            except Exception as exc:
                cleanup_maps = [{"map": map_name, "error": str(exc)}]
        else:
            cleanup_maps = []
        summary["cleanup"] = {"maps": cleanup_maps}

    project_after = project_path.stat()
    summary["project_file_unchanged"] = (
        project_before.st_size == project_after.st_size
        and project_before.st_mtime_ns == project_after.st_mtime_ns
    )
    summary["synthetic_source_fingerprint_unchanged"] = (
        vector_before == fingerprint_tree(vector_root)
        and raster_before == fingerprint_tree(raster_root)
    )
    if not summary["project_file_unchanged"]:
        raise RuntimeError("The open .aprx file changed during v0.17 validation")
    if not summary["synthetic_source_fingerprint_unchanged"]:
        raise RuntimeError("Synthetic validation inputs changed during v0.17 validation")

    output = ROOT / "outputs" / "bridge_runtime_tests" / "live_v017_validation_latest.json"
    output.write_text(json.dumps(summary, ensure_ascii=False, indent=2, default=str), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
