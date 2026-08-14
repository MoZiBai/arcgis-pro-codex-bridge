from __future__ import annotations

import hashlib
import json
import shutil
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


def wait_active(server: ArcGISMCP, map_name: str) -> None:
    for _ in range(60):
        if server._run_live("status", {}).get("active_map") == map_name:
            return
        time.sleep(0.25)
    raise RuntimeError(f"Temporary map did not become active: {map_name}")


def write_ascii_grid(path: Path) -> None:
    rows = [
        "ncols 8",
        "nrows 8",
        "xllcorner 0",
        "yllcorner 0",
        "cellsize 100",
        "NODATA_value -9999",
    ]
    for y in range(8):
        rows.append(" ".join(str(100 + x * 20 + y * 35 + ((x - 3) ** 2 + (y - 4) ** 2) * 4) for x in range(8)))
    path.write_text("\n".join(rows) + "\n", encoding="ascii")


def remove_path(path: Path) -> bool:
    for _ in range(20):
        try:
            if path.is_dir():
                shutil.rmtree(path)
            elif path.exists():
                path.unlink()
            return not path.exists()
        except OSError:
            time.sleep(0.25)
    return not path.exists()


def main() -> int:
    server = ArcGISMCP(ROOT / "config.json")
    server.live_timeout = min(server.live_timeout, 180)
    status = server._run_live("status", {})
    if status.get("protocol_version") != "0.27.3":
        raise RuntimeError(f"Expected protocol 0.27.3, got {status.get('protocol_version')}")
    actions = set(status.get("capabilities") or [])
    required = {
        "manage_exploratory_analysis",
        "manage_report_elements",
        "manage_presentation_elements",
        "configure_specialized_renderer",
        "inspect_printers",
        "manage_report_composition",
        "configure_presentation_transition",
        "evaluate_attribute_rules",
    }
    if len(actions) < 236 or not required.issubset(actions):
        raise RuntimeError(f"Expected at least 236 actions including v0.27 additions; got {len(actions)}")

    vector_root = ROOT / "outputs" / "bridge_runtime_tests" / "vector_inputs_v015"
    manifest = json.loads((vector_root / "manifest.json").read_text(encoding="utf-8"))
    residents = Path(manifest["residents"])
    source_before = fingerprint_tree(vector_root)
    project_path = Path(str(status["project"]))
    project_before = project_path.stat()

    marker = str(int(time.time()))
    scene_name = f"CodexV027Scene_{marker}"
    map_name = f"CodexV027Map_{marker}"
    report_name = f"CodexV027Report_{marker}"
    presentation_name = f"CodexV027Presentation_{marker}"
    test_root = ROOT / "outputs" / "bridge_runtime_tests" / f"v027_{marker}"
    ascii_grid = test_root / "surface.asc"
    raster = test_root / "surface.tif"
    tin = test_root / "surface_tin"
    test_root.mkdir(parents=True, exist_ok=True)
    write_ascii_grid(ascii_grid)

    created_scene = created_map = created_report = created_presentation = False
    summary: dict[str, object] = {
        "handshake": {
            "addin_version": status.get("addin_version"),
            "protocol_version": status.get("protocol_version"),
            "action_count": len(actions),
        },
        "source_data_modified": False,
        "project_saved": False,
    }
    cleanup: dict[str, object] = {}
    try:
        printers = server._run_live("inspect_printers", {"max_results": 50})
        if printers.get("printing_executed") is not False or printers.get("external_state_modified") is not False:
            raise RuntimeError(f"Printer inspection safety metadata is invalid: {printers}")
        summary["printer_inspection"] = printers

        server._run_live(
            "create_map",
            {"map_name": scene_name, "map_type": "Scene", "viewing_mode": "SceneLocal", "basemap": "None", "open_map": True},
        )
        created_scene = True
        wait_active(server, scene_name)
        observer = {"x": 111.3, "y": 30.7, "z": 1500.0, "wkid": 4326, "pitch": -35.0, "heading": 25.0}
        targets = [
            {"x": 111.31, "y": 30.71, "z": 500.0, "wkid": 4326},
            {"x": 111.32, "y": 30.695, "z": 550.0, "wkid": 4326},
        ]
        initial = server._run_live("manage_exploratory_analysis", {"operation": "inspect", "map_name": scene_name})
        server._run_live(
            "manage_exploratory_analysis",
            {"operation": "add", "map_name": scene_name, "analysis_type": "viewshed", "observer": observer, "maximum_distance": 5000},
        )
        server._run_live(
            "manage_exploratory_analysis",
            {"operation": "add", "map_name": scene_name, "analysis_type": "view_dome", "observer": observer, "maximum_distance": 3000},
        )
        server._run_live(
            "manage_exploratory_analysis",
            {"operation": "add", "map_name": scene_name, "analysis_type": "line_of_sight", "observer": observer, "targets": targets, "maximum_distance": 5000},
        )
        analyses = server._run_live("manage_exploratory_analysis", {"operation": "inspect", "map_name": scene_name})
        if analyses.get("analysis_count") != 3:
            raise RuntimeError(f"Expected three temporary exploratory analyses: {analyses}")
        cleared = server._run_live(
            "manage_exploratory_analysis",
            {"operation": "clear", "map_name": scene_name, "confirm_clear": True},
        )
        if (cleared.get("state") or {}).get("analysis_count") != 0:
            raise RuntimeError(f"Temporary exploratory analyses were not cleared: {cleared}")
        summary["exploratory_analysis"] = {"initial": initial, "created": analyses, "cleared": cleared}

        server._run_live(
            "run_geoprocessing",
            {"tool": "conversion.ASCIIToRaster", "args": [str(ascii_grid), str(raster), "FLOAT"], "overwrite_output": True, "add_outputs_to_map": False},
        )
        server._run_live(
            "run_geoprocessing",
            {"tool": "ddd.RasterTin", "args": [str(raster), str(tin), 1.0, 5000], "overwrite_output": True, "add_outputs_to_map": False},
        )
        server._run_live("add_data", {"map_name": scene_name, "data_paths": [str(tin)]})
        specialized = server._run_live(
            "configure_specialized_renderer",
            {
                "operation": "apply",
                "map_name": scene_name,
                "layer_name": tin.name,
                "renderer_type": "face_elevation",
                "class_count": 7,
                "classification": "NaturalBreaks",
                "color_stops": ["#173B57", "#4D8077", "#A7B88A", "#D8C99B", "#F2E8D5"],
            },
        )
        contours = server._run_live(
            "configure_specialized_renderer",
            {
                "operation": "apply",
                "map_name": scene_name,
                "layer_name": tin.name,
                "renderer_type": "contour",
                "contour_interval": 50,
                "index_contour_factor": 5,
            },
        )
        specialized_state = server._run_live(
            "configure_specialized_renderer",
            {"operation": "inspect", "map_name": scene_name, "layer_name": tin.name},
        )
        summary["specialized_renderer"] = {"surface": specialized, "contours": contours, "state": specialized_state}

        server._run_live(
            "create_map",
            {"map_name": map_name, "map_type": "Map", "viewing_mode": "Map", "basemap": "None", "open_map": True},
        )
        created_map = True
        wait_active(server, map_name)
        server._run_live("add_data", {"map_name": map_name, "data_paths": [str(residents)]})
        server._run_live(
            "create_report",
            {"report_name": report_name, "map_name": map_name, "map_member_name": residents.name, "open_report": False},
        )
        created_report = True
        report_add = server._run_live(
            "manage_report_elements",
            {
                "operation": "add",
                "report_name": report_name,
                "element_type": "paragraph",
                "element_name": "CodexV027Paragraph",
                "text": "Codex v0.27 temporary report element",
                "size": 8,
            },
        )
        report_state = server._run_live("manage_report_elements", {"operation": "inspect", "report_name": report_name})
        report_composition = server._run_live(
            "manage_report_composition", {"operation": "inspect", "report_name": report_name}
        )
        report_remove = server._run_live(
            "manage_report_elements",
            {"operation": "remove", "report_name": report_name, "element_names": ["CodexV027Paragraph"], "confirm_remove": True},
        )
        summary["report_elements"] = {
            "added": report_add, "state": report_state,
            "composition": report_composition, "removed": report_remove,
        }

        server._run_live("create_presentation", {"presentation_name": presentation_name, "aspect_width": 16, "aspect_height": 9})
        created_presentation = True
        server._run_live(
            "add_presentation_page",
            {"presentation_name": presentation_name, "page_type": "blank", "blank_template": "Blank"},
        )
        for element in (
            {
                "element_type": "shape", "element_name": "CodexV027Shape",
                "bounds": {"xmin": 1, "ymin": 1, "xmax": 5, "ymax": 3},
                "shape": "RoundedRectangle", "fill_color": "#D9E7EF", "outline_color": "#325A70",
            },
            {
                "element_type": "text", "element_name": "CodexV027Text",
                "bounds": {"xmin": 1.4, "ymin": 1.5, "xmax": 4.6, "ymax": 2.5},
                "text": "Codex v0.27", "size": 18,
            },
        ):
            server._run_live(
                "manage_presentation_elements",
                {"operation": "add", "presentation_name": presentation_name, "page_index": 0, **element},
            )
        transition = server._run_live(
            "configure_presentation_transition",
            {
                "operation": "apply", "presentation_name": presentation_name,
                "page_index": 0, "transition_type": "Fade", "duration": 0.5,
                "speaker_notes": "Temporary Codex v0.27 validation page",
            },
        )
        grouped = server._run_live(
            "manage_presentation_elements",
            {
                "operation": "group", "presentation_name": presentation_name, "page_index": 0,
                "element_names": ["CodexV027Shape", "CodexV027Text"], "element_name": "CodexV027Group",
            },
        )
        ungrouped = server._run_live(
            "manage_presentation_elements",
            {"operation": "ungroup", "presentation_name": presentation_name, "page_index": 0, "element_name": "CodexV027Group"},
        )
        presentation_state = server._run_live(
            "manage_presentation_elements",
            {"operation": "inspect", "presentation_name": presentation_name, "page_index": 0},
        )
        removed = server._run_live(
            "manage_presentation_elements",
            {
                "operation": "remove", "presentation_name": presentation_name, "page_index": 0,
                "element_names": ["CodexV027Shape", "CodexV027Text"], "confirm_remove": True,
            },
        )
        summary["presentation_elements"] = {
            "transition": transition, "grouped": grouped, "ungrouped": ungrouped,
            "state": presentation_state, "removed": removed,
        }
    finally:
        if created_report:
            try:
                cleanup["report"] = server._run_live("remove_report", {"report_name": report_name, "confirm_remove": True})
            except Exception as exc:
                cleanup["report"] = {"name": report_name, "error": str(exc)}
        if created_presentation:
            try:
                cleanup["presentation"] = server._run_live(
                    "remove_presentation", {"presentation_name": presentation_name, "confirm_remove": True}
                )
            except Exception as exc:
                cleanup["presentation"] = {"name": presentation_name, "error": str(exc)}
        for key, name, created in (("map", map_name, created_map), ("scene", scene_name, created_scene)):
            if created:
                try:
                    cleanup[key] = server._run_live("remove_map", {"map_name": name, "confirm_remove": True})
                except Exception as exc:
                    cleanup[key] = {"name": name, "error": str(exc)}
        time.sleep(1)
        cleanup["test_files_removed"] = remove_path(test_root)
        summary["cleanup"] = cleanup

    project_after = project_path.stat()
    summary["project_file_unchanged"] = (
        project_before.st_size == project_after.st_size
        and project_before.st_mtime_ns == project_after.st_mtime_ns
    )
    summary["synthetic_source_fingerprint_unchanged"] = source_before == fingerprint_tree(vector_root)
    if not summary["project_file_unchanged"]:
        raise RuntimeError("The open .aprx file changed during v0.27 validation")
    if not summary["synthetic_source_fingerprint_unchanged"]:
        raise RuntimeError("Synthetic source data changed during v0.27 validation")
    if not cleanup.get("test_files_removed"):
        raise RuntimeError(f"Temporary v0.27 synthetic files were not fully removed: {test_root}")

    output = ROOT / "outputs" / "bridge_runtime_tests" / "live_v027_validation_latest.json"
    output.write_text(json.dumps(summary, ensure_ascii=False, indent=2, default=str), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
