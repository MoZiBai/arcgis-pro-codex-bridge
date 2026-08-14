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
    fingerprints: dict[str, str] = {}
    for path in sorted(item for item in root.rglob("*") if item.is_file()):
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(block)
        fingerprints[path.relative_to(root).as_posix()] = digest.hexdigest()
    return fingerprints


def main() -> int:
    server = ArcGISMCP(ROOT / "config.json")
    server.live_timeout = min(server.live_timeout, 120)
    status = server._run_live("status", {})
    if status.get("protocol_version") not in {"0.15.0", "0.16.0", "0.17.0", "0.18.0", "0.19.0", "0.20.0", "0.21.0", "0.22.0", "0.23.0", "0.23.1", "0.23.2", "0.24.0", "0.25.0", "0.26.0", "0.27.0", "0.27.1", "0.27.2", "0.27.3"}:
        raise RuntimeError(
            f"Expected protocol 0.15.0 or compatible 0.16.0, got {status.get('protocol_version')}"
        )

    capabilities = server._run_live("capabilities", {})
    actions = set(capabilities.get("actions") or [])
    if len(actions) < 154 or "apply_vector_symbology" not in actions:
        raise RuntimeError(
            f"Expected 154 actions including apply_vector_symbology; got {len(actions)}"
        )

    marker = str(int(time.time()))
    map_name = f"CodexV015Symbology_{marker}"
    output_dir = ROOT / "outputs" / "bridge_runtime_tests"
    output_dir.mkdir(parents=True, exist_ok=True)
    synthetic_root = ROOT / "outputs" / "bridge_runtime_tests" / "vector_inputs_v015"
    manifest_path = synthetic_root / "manifest.json"
    if not manifest_path.exists():
        raise FileNotFoundError(
            "Vector validation inputs are missing; run "
            "scripts/create_vector_validation_data.py first"
        )
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    districts = Path(manifest["districts"])
    residents = Path(manifest["residents"])
    if not districts.parent.exists() or districts.parent != residents.parent:
        raise FileNotFoundError(f"Incomplete vector validation inputs: {manifest}")

    project_path = Path(str(status["project"]))
    project_before = project_path.stat()
    source_before = fingerprint_tree(synthetic_root)
    palette = ["#F3E9D2", "#84A59D", "#52796F", "#354F52", "#CA6702"]
    cases = [
        (
            "graduated_symbols",
            "validation_residents",
            {
                "field": "POP",
                "class_count": 5,
                "minimum_size": 3,
                "maximum_size": 18,
                "color_stops": palette,
                "symbol": {"color": "#CA6702", "size": 6, "marker": "Circle"},
            },
        ),
        (
            "proportional",
            "validation_residents",
            {
                "field": "POP",
                "minimum_size": 3,
                "maximum_size": 22,
                "legend_symbol_count": 4,
                "flannery_compensation": True,
                "symbol": {"color": "#C8553DCC", "size": 6, "marker": "Circle"},
            },
        ),
        (
            "unclassed_colors",
            "validation_residents",
            {
                "field": "POP",
                "lower_label": "Lower population",
                "upper_label": "Higher population",
                "color_stops": palette,
                "symbol": {"color": "#52796F", "size": 7, "marker": "Circle"},
            },
        ),
        (
            "heat_map",
            "validation_residents",
            {
                "weight_field": "POP",
                "radius": 18,
                "rendering_quality": 2,
                "lower_label": "Low density",
                "upper_label": "High density",
                "color_stops": ["#FFF7E6", "#F4A261", "#E76F51", "#7F1D1D"],
            },
        ),
        (
            "dot_density",
            "validation_districts",
            {
                "value_fields": ["METRIC_A"],
                "dot_size": 1.5,
                "dot_value": 0.01,
                "symbol_label": "Area dot",
                "unit_label": "square degrees",
                "color_stops": ["#1D3557", "#E63946"],
                "background_symbol": {
                    "color": "#F7F4EA",
                    "outline_color": "#B8B0A5",
                    "width": 0.6,
                },
            },
        ),
        (
            "pie_chart",
            "validation_districts",
            {
                "chart_fields": ["METRIC_A", "METRIC_B"],
                "size_option": "fixed",
                "fixed_size": 12,
                "orientation": "clockwise",
                "show_outline": True,
                "color_stops": ["#2A9D8F", "#E9C46A", "#E76F51"],
            },
        ),
        (
            "bar_chart",
            "validation_districts",
            {
                "chart_fields": ["METRIC_A", "METRIC_B"],
                "orientation": "vertical",
                "bar_width": 5,
                "maximum_bar_length": 20,
                "show_axes": True,
                "color_stops": ["#264653", "#2A9D8F", "#E9C46A"],
            },
        ),
        (
            "stacked_chart",
            "validation_districts",
            {
                "chart_fields": ["METRIC_A", "METRIC_B"],
                "orientation": "horizontal",
                "size_option": "fixed",
                "stack_length": 20,
                "stack_width": 6,
                "show_outline": True,
                "color_stops": ["#457B9D", "#A8DADC", "#E63946"],
            },
        ),
    ]

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
    results: list[dict[str, object]] = []
    try:
        server._run_live("create_map", {"map_name": map_name, "open_map": False})
        map_created = True
        server._run_live(
            "add_data",
            {
                "map_name": map_name,
                "data_paths": [str(districts), str(residents)],
            },
        )
        for renderer_name, layer_name, renderer_payload in cases:
            payload = {
                "map_name": map_name,
                "layer_name": layer_name,
                "renderer": renderer_name,
                **renderer_payload,
            }
            applied = server._run_live("apply_vector_symbology", payload)
            described = server._run_live(
                "describe_layer",
                {"map_name": map_name, "layer_name": layer_name},
            )
            actual_class = (described.get("renderer") or {}).get("type")
            if not actual_class or actual_class != applied.get("renderer_class"):
                raise RuntimeError(
                    f"Renderer did not round-trip for {renderer_name}: "
                    f"applied={applied}, described={described}"
                )
            results.append(
                {
                    "renderer": renderer_name,
                    "layer": layer_name,
                    "renderer_class": actual_class,
                }
            )
        summary["renderers"] = results
    finally:
        cleanup: dict[str, object] = {}
        if map_created:
            try:
                cleanup["maps"] = [
                    server._run_live(
                        "remove_map", {"map_name": map_name, "confirm_remove": True}
                    )
                ]
            except Exception as exc:
                cleanup["maps"] = [{"map": map_name, "error": str(exc)}]
        else:
            cleanup["maps"] = []
        summary["cleanup"] = cleanup

    project_after = project_path.stat()
    source_after = fingerprint_tree(synthetic_root)
    summary["project_file_unchanged"] = (
        project_before.st_size == project_after.st_size
        and project_before.st_mtime_ns == project_after.st_mtime_ns
    )
    summary["synthetic_source_fingerprint_unchanged"] = source_before == source_after
    if not summary["project_file_unchanged"]:
        raise RuntimeError("The open .aprx file changed during v0.15 validation")
    if not summary["synthetic_source_fingerprint_unchanged"]:
        raise RuntimeError("Synthetic source data changed during v0.15 validation")

    output = output_dir / "live_v015_validation_latest.json"
    output.write_text(
        json.dumps(summary, ensure_ascii=False, indent=2, default=str), encoding="utf-8"
    )
    print(json.dumps(summary, ensure_ascii=False, indent=2, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
