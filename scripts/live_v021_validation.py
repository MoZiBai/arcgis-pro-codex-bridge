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
    if status.get("protocol_version") not in {"0.21.0", "0.22.0", "0.23.0", "0.23.1", "0.23.2", "0.24.0", "0.25.0", "0.26.0", "0.27.0", "0.27.1", "0.27.2", "0.27.3", "0.28.0", "0.28.1", "0.28.2"}:
        raise RuntimeError(f"Expected protocol 0.21.0 or a compatible later version, got {status.get('protocol_version')}")
    actions = set(status.get("capabilities") or [])
    required = {
        "get_active_view_context",
        "configure_active_view_context",
        "navigate_map_view",
        "flash_features",
        "show_feature_popup",
        "close_feature_popups",
        "manage_feature_cache",
        "capture_view_thumbnail",
    }
    if len(actions) < 202 or not required.issubset(actions):
        raise RuntimeError(f"Expected at least 202 actions including v0.21 additions; got {len(actions)}")

    vector_root = ROOT / "outputs" / "bridge_runtime_tests" / "vector_inputs_v015"
    manifest = json.loads((vector_root / "manifest.json").read_text(encoding="utf-8"))
    residents = Path(manifest["residents"])
    source_before = fingerprint_tree(vector_root)
    project_path = Path(str(status["project"]))
    project_before = project_path.stat()
    thumbnail = ROOT / "outputs" / "bridge_runtime_tests" / "live_v021_thumbnail.png"
    if thumbnail.exists():
        thumbnail.unlink()

    marker = str(int(time.time()))
    map_name = f"CodexV021_{marker}"
    layer_name = "validation_residents"
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
            raise RuntimeError("Temporary v0.21 map did not become active")
        server._run_live("add_data", {"map_name": map_name, "data_paths": [str(residents)]})

        context_before = server._run_live("get_active_view_context", {"map_name": map_name})
        selected = server._run_live(
            "configure_active_view_context",
            {
                "map_name": map_name,
                "selected_layer_names": [layer_name],
                "drawing_paused": True,
            },
        )
        toc_configured = server._run_live(
            "configure_active_view_context",
            {"map_name": map_name, "toc_content": "DrawingOrder"},
        )
        resumed = server._run_live(
            "configure_active_view_context",
            {"map_name": map_name, "drawing_paused": False, "redraw": True},
        )
        if layer_name not in selected.get("selected_layers", []):
            raise RuntimeError(f"Layer TOC selection did not apply: {selected}")
        if resumed.get("drawing_paused") is not False:
            raise RuntimeError(f"Drawing did not resume: {resumed}")

        zoomed_out = server._run_live(
            "navigate_map_view",
            {"map_name": map_name, "operation": "zoom_out_fixed", "duration_ms": 0},
        )
        zoomed_in = server._run_live(
            "navigate_map_view",
            {"map_name": map_name, "operation": "zoom_in_fixed", "duration_ms": 0},
        )
        flashed = server._run_live(
            "flash_features",
            {"map_name": map_name, "layer_name": layer_name, "object_ids": [1]},
        )
        popup = server._run_live(
            "show_feature_popup",
            {"map_name": map_name, "map_member_name": layer_name, "object_id": 1},
        )
        closed = server._run_live("close_feature_popups", {"map_name": map_name})
        cache = server._run_live(
            "manage_feature_cache", {"map_name": map_name, "operation": "inspect"}
        )
        captured = server.call_tool(
            "live_capture_view_thumbnail",
            {
                "map_name": map_name,
                "output_file": str(thumbnail),
                "width": 640,
                "height": 480,
            },
        )
        if not thumbnail.exists() or thumbnail.stat().st_size == 0:
            raise RuntimeError(f"View thumbnail was not created: {captured}")

        summary.update(
            {
                "context": {
                    "before": context_before,
                    "selected": selected,
                    "toc_configured": toc_configured,
                    "resumed": resumed,
                },
                "navigation": {"zoomed_out": zoomed_out, "zoomed_in": zoomed_in},
                "feature_ui": {"flashed": flashed, "popup": popup, "closed": closed},
                "feature_cache": cache,
                "thumbnail": {
                    "result": captured,
                    "size": thumbnail.stat().st_size,
                    "sha256": hashlib.sha256(thumbnail.read_bytes()).hexdigest(),
                },
            }
        )
    finally:
        try:
            server._run_live("close_feature_popups", {"map_name": map_name})
        except Exception:
            pass
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
        raise RuntimeError("The open .aprx file changed during v0.21 validation")
    if not summary["synthetic_source_fingerprint_unchanged"]:
        raise RuntimeError("Synthetic source data changed during v0.21 validation")

    output = ROOT / "outputs" / "bridge_runtime_tests" / "live_v021_validation_latest.json"
    output.write_text(json.dumps(summary, ensure_ascii=False, indent=2, default=str), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
