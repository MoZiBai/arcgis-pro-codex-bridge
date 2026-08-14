from __future__ import annotations

import hashlib
import json
import math
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


def assert_finite_or_none(result: dict[str, object], keys: tuple[str, ...]) -> None:
    for key in keys:
        value = result.get(key)
        if value is not None and (not isinstance(value, (int, float)) or not math.isfinite(value)):
            raise RuntimeError(f"Camera response contains a non-finite {key}: {result}")


def main() -> int:
    server = ArcGISMCP(ROOT / "config.json")
    server.live_timeout = min(server.live_timeout, 120)
    status = server._run_live("status", {})
    if status.get("protocol_version") not in {"0.25.0", "0.26.0", "0.27.0", "0.27.1", "0.27.2", "0.27.3"}:
        raise RuntimeError(f"Expected protocol 0.25.0 or a compatible later version, got {status.get('protocol_version')}")

    vector_root = ROOT / "outputs" / "bridge_runtime_tests" / "vector_inputs_v015"
    manifest = json.loads((vector_root / "manifest.json").read_text(encoding="utf-8"))
    residents = Path(manifest["residents"])
    source_before = fingerprint_tree(vector_root)
    project_path = Path(str(status["project"]))
    project_before = project_path.stat()

    marker = str(int(time.time()))
    map_name = f"CodexV025_{marker}"
    layer_name = "validation_residents"
    created = False
    summary: dict[str, object] = {
        "handshake": {
            "addin_version": status.get("addin_version"),
            "protocol_version": status.get("protocol_version"),
            "action_count": len(status.get("capabilities") or []),
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
            raise RuntimeError("Temporary v0.25 map did not become active")

        camera = server._run_live(
            "set_camera",
            {"map_name": map_name, "x": 0.0, "y": 0.0, "scale": 1000000.0},
        )
        assert_finite_or_none(camera, ("x", "y", "z", "scale", "heading", "pitch", "roll"))

        server._run_live("add_data", {"map_name": map_name, "data_paths": [str(residents)]})
        schema = server._run_live(
            "get_layer_schema", {"map_name": map_name, "layer_name": layer_name}
        )
        required_top_level = {"has_joins", "has_relates", "select_related_data", "rules"}
        missing = sorted(required_top_level - set(schema))
        if missing:
            raise RuntimeError(f"Advanced layer schema fields are missing: {missing}; schema={schema}")
        rules = schema.get("rules") or {}
        required_rules = {
            "dataset_type",
            "joined_table",
            "archive_enabled",
            "attachments_enabled",
            "editor_tracking_enabled",
            "validation_status_field",
            "editor_tracking_fields",
            "subtypes",
            "indexes",
            "attribute_rules",
            "contingencies",
            "controller_datasets",
            "inspection_errors",
        }
        missing_rules = sorted(required_rules - set(rules))
        if missing_rules:
            raise RuntimeError(f"Advanced geodatabase inventory is incomplete: {missing_rules}; rules={rules}")
        if schema.get("source_data_modified") is not False:
            raise RuntimeError("Layer schema inventory did not affirm source_data_modified=false")

        summary.update({"camera": camera, "layer_schema": schema})
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
        raise RuntimeError("The open .aprx file changed during v0.25 validation")
    if not summary["synthetic_source_fingerprint_unchanged"]:
        raise RuntimeError("Synthetic source data changed during v0.25 validation")

    output = ROOT / "outputs" / "bridge_runtime_tests" / "live_v025_validation_latest.json"
    output.write_text(json.dumps(summary, ensure_ascii=False, indent=2, default=str), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
