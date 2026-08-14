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
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        fingerprints[path.relative_to(root).as_posix()] = digest
    return fingerprints


def main() -> int:
    server = ArcGISMCP(ROOT / "config.json")
    server.live_timeout = min(server.live_timeout, 120)
    status = server._run_live("status", {})
    if status.get("protocol_version") not in {"0.16.0", "0.17.0", "0.18.0", "0.19.0", "0.20.0", "0.21.0", "0.22.0", "0.23.0", "0.23.1", "0.23.2", "0.24.0", "0.25.0", "0.26.0", "0.27.0", "0.27.1", "0.27.2", "0.27.3", "0.28.0", "0.28.1", "0.28.2"}:
        raise RuntimeError(
            f"Expected protocol 0.16.0 or a compatible newer bridge, got {status.get('protocol_version')}"
        )

    raster_root = ROOT / "outputs" / "bridge_runtime_tests" / "raster_inputs_v016"
    manifest_path = raster_root / "manifest.json"
    if not manifest_path.exists():
        raise FileNotFoundError(
            "Raster validation inputs are missing; run "
            "scripts/create_raster_validation_data.py first"
        )
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    raster_paths = [
        Path(manifest["rgb"]),
        Path(manifest["categorical"]),
        Path(manifest["vector_field"]),
        Path(manifest["elevation"]),
    ]
    if any(not path.exists() for path in raster_paths):
        raise FileNotFoundError(f"Incomplete raster validation inputs: {manifest}")

    marker = str(int(time.time()))
    map_name = f"CodexV016Raster_{marker}"
    project_path = Path(str(status["project"]))
    project_before = project_path.stat()
    source_before = fingerprint_tree(raster_root)
    cases = [
        (
            "rgb",
            "v016_rgb.tif",
            {
                "red_band_index": 0,
                "green_band_index": 1,
                "blue_band_index": 2,
                "stretch_type": "percent_minimum_maximum",
                "gamma_r": 1.05,
                "gamma_g": 1.0,
                "gamma_b": 0.95,
                "min_percent": 1.5,
                "max_percent": 1.5,
                "resampling_type": "bilinear_interpolation",
            },
        ),
        (
            "unique_value",
            "v016_categories.tif",
            {
                "field": "Value",
                "color_stops": ["#264653", "#2A9D8F", "#E9C46A", "#E76F51"],
                "resampling_type": "nearest_neighbor",
            },
        ),
        (
            "colormap",
            "v016_categories.tif",
            {
                "colormap_entries": [
                    {"value": 1, "color": "#264653", "label": "Northwest"},
                    {"value": 2, "color": "#2A9D8F", "label": "Northeast"},
                    {"value": 3, "color": "#E9C46A", "label": "Southwest"},
                    {"value": 4, "color": "#E76F51", "label": "Southeast"},
                ],
            },
        ),
        (
            "color_map",
            "v016_categories.tif",
            {"colormap_file": manifest["colormap"]},
        ),
        (
            "vector_field",
            "v016_vector_field.tif",
            {
                "magnitude_band_index": 0,
                "direction_band_index": 1,
                "is_uv_components": False,
                "symbolization_type": "single_arrow",
                "resampling_type": "vector_average",
            },
        ),
        (
            "shaded_relief",
            "v016_elevation.tif",
            {
                "color_stops": ["#F1E8C8", "#A7C4A0", "#557C83", "#394867"],
                "hillshade_type": "multidirectional",
                "scaling_type": "adjusted",
                "remove_edge_effect": True,
                "pixel_size_factor": 1.0,
                "pixel_size_power": 1.0,
                "z_factor": 1.0,
                "brightness": 4,
                "contrast": 6,
            },
        ),
    ]

    summary: dict[str, object] = {
        "handshake": {
            "addin_version": status.get("addin_version"),
            "protocol_version": status.get("protocol_version"),
            "action_count": len(status.get("capabilities") or []),
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
            {"map_name": map_name, "data_paths": [str(path) for path in raster_paths]},
        )
        for colorizer_name, layer_name, colorizer_payload in cases:
            applied = server._run_live(
                "apply_raster_symbology",
                {
                    "map_name": map_name,
                    "layer_name": layer_name,
                    "colorizer": colorizer_name,
                    **colorizer_payload,
                },
            )
            described = server._run_live(
                "describe_layer",
                {"map_name": map_name, "layer_name": layer_name},
            )
            actual_class = (described.get("renderer") or {}).get("type")
            if not actual_class or actual_class != applied.get("colorizer_class"):
                raise RuntimeError(
                    f"Colorizer did not round-trip for {colorizer_name}: "
                    f"applied={applied}, described={described}"
                )
            results.append(
                {
                    "colorizer": colorizer_name,
                    "layer": layer_name,
                    "colorizer_class": actual_class,
                }
            )
        summary["colorizers"] = results
    finally:
        if map_created:
            try:
                cleanup_maps = [
                    server._run_live(
                        "remove_map", {"map_name": map_name, "confirm_remove": True}
                    )
                ]
            except Exception as exc:
                cleanup_maps = [{"map": map_name, "error": str(exc)}]
        else:
            cleanup_maps = []
        summary["cleanup"] = {"maps": cleanup_maps}

    project_after = project_path.stat()
    source_after = fingerprint_tree(raster_root)
    summary["project_file_unchanged"] = (
        project_before.st_size == project_after.st_size
        and project_before.st_mtime_ns == project_after.st_mtime_ns
    )
    summary["synthetic_source_fingerprint_unchanged"] = source_before == source_after
    if not summary["project_file_unchanged"]:
        raise RuntimeError("The open .aprx file changed during v0.16 validation")
    if not summary["synthetic_source_fingerprint_unchanged"]:
        raise RuntimeError("Synthetic raster inputs changed during v0.16 validation")

    output = ROOT / "outputs" / "bridge_runtime_tests" / "live_v016_validation_latest.json"
    output.write_text(
        json.dumps(summary, ensure_ascii=False, indent=2, default=str), encoding="utf-8"
    )
    print(json.dumps(summary, ensure_ascii=False, indent=2, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
