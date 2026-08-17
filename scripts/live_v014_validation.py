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
    if status.get("protocol_version") not in {"0.14.1", "0.15.0", "0.16.0", "0.17.0", "0.18.0", "0.19.0", "0.20.0", "0.21.0", "0.22.0", "0.23.0", "0.23.1", "0.23.2", "0.24.0", "0.25.0", "0.26.0", "0.27.0", "0.27.1", "0.27.2", "0.27.3", "0.28.0", "0.28.1", "0.28.2", "0.29.0"}:
        raise RuntimeError(
            f"Expected protocol 0.14.1 or compatible 0.15.0, got {status.get('protocol_version')}"
        )
    capabilities = server._run_live("capabilities", {})
    actions = set(capabilities.get("actions") or [])
    required = {
        "configure_map_properties",
        "configure_layout_page",
        "save_map_file",
        "save_layout_file",
        "configure_bookmark",
        "move_bookmark",
        "import_bookmarks",
        "export_bookmarks",
    }
    if len(actions) < 154 or not required.issubset(actions):
        raise RuntimeError(
            f"Expected 154 actions including v0.14 additions; got {len(actions)}"
        )

    marker = str(int(time.time()))
    source_map = f"CodexV014Source_{marker}"
    import_map = f"CodexV014Import_{marker}"
    layout_name = f"CodexV014Layout_{marker}"
    output_dir = ROOT / "outputs" / "bridge_runtime_tests"
    output_dir.mkdir(parents=True, exist_ok=True)
    mapx = output_dir / "live_v014_map_latest.mapx"
    pagx = output_dir / "live_v014_layout_latest.pagx"
    bkmx = output_dir / "live_v014_bookmarks_latest.bkmx"
    synthetic_gdb = (
        ROOT
        / "outputs"
        / "bridge_runtime_tests"
        / "vector_inputs_v015"
        / "vector_inputs_v015.gdb"
    )
    districts = synthetic_gdb / "validation_districts"
    if not synthetic_gdb.exists():
        raise FileNotFoundError(f"Synthetic validation input is missing: {synthetic_gdb}")

    summary: dict[str, object] = {
        "handshake": {
            "addin_version": status.get("addin_version"),
            "protocol_version": status.get("protocol_version"),
            "action_count": len(actions),
        },
        "source_data_modified": False,
        "project_saved": False,
    }
    created_maps: list[str] = []
    layout_created = False
    try:
        server._run_live("create_map", {"map_name": source_map, "open_map": False})
        created_maps.append(source_map)
        server._run_live(
            "add_data", {"map_name": source_map, "data_paths": [str(districts)]}
        )
        map_properties = server._run_live(
            "configure_map_properties",
            {
                "map_name": source_map,
                "reference_scale": 1000000,
                "background_color": "#F5F4EF",
                "snap_to_scales": True,
                "scales": [
                    {"value": 500000, "alias": "1:500,000"},
                    {"value": 1000000, "alias": "1:1,000,000"},
                    {"value": 2000000, "alias": "1:2,000,000"},
                ],
                "default_scale": 1000000,
                "default_rotation": 0,
            },
        )
        if len(map_properties.get("scales") or []) != 3:
            raise RuntimeError(f"Map scales did not round-trip: {map_properties}")
        map_file = server._run_live(
            "save_map_file",
            {
                "map_name": source_map,
                "output_file": str(mapx),
                "overwrite": True,
            },
        )
        if not mapx.exists() or mapx.stat().st_size == 0:
            raise RuntimeError("MAPX output was not created")

        server._run_live("open_map", {"map_name": source_map})
        first = server._run_live(
            "create_bookmark",
            {
                "map_name": source_map,
                "bookmark_name": "Validation Bookmark A",
                "description": "Initial bookmark",
            },
        )
        second = server._run_live(
            "create_bookmark",
            {
                "map_name": source_map,
                "bookmark_name": "Validation Bookmark B",
                "description": "Second bookmark",
            },
        )
        configured = server._run_live(
            "configure_bookmark",
            {
                "map_name": source_map,
                "bookmark_name": "Validation Bookmark A",
                "new_name": "Validation Bookmark Updated",
                "description": "Updated by v0.14 validation",
                "folder_name": "Codex Validation",
                "use_active_view_extent": True,
            },
        )
        moved = server._run_live(
            "move_bookmark",
            {
                "map_name": source_map,
                "bookmark_name": "Validation Bookmark B",
                "index": 0,
            },
        )
        bookmark_export = server._run_live(
            "export_bookmarks",
            {"map_name": source_map, "output_file": str(bkmx)},
        )
        if not bkmx.exists() or bkmx.stat().st_size == 0:
            raise RuntimeError("BKMX output was not created")

        server._run_live("create_map", {"map_name": import_map, "open_map": False})
        created_maps.append(import_map)
        bookmark_import = server._run_live(
            "import_bookmarks",
            {"map_name": import_map, "input_file": str(bkmx)},
        )
        imported_names = set(bookmark_import.get("imported") or [])
        if not {"Validation Bookmark Updated", "Validation Bookmark B"}.issubset(
            imported_names
        ):
            raise RuntimeError(f"Bookmarks did not import: {bookmark_import}")

        server._run_live(
            "create_layout",
            {
                "layout_name": layout_name,
                "map_name": source_map,
                "map_frame_name": "Validation Frame",
                "legend": False,
                "north_arrow": False,
                "scale_bar": False,
                "open_layout": False,
            },
        )
        layout_created = True
        page = server._run_live(
            "configure_layout_page",
            {
                "layout_name": layout_name,
                "width": 28.0,
                "height": 20.0,
                "units": "centimeters",
                "show_guides": True,
                "show_margin": True,
                "show_rulers": True,
                "smallest_ruler_division": 0.1,
                "resize_elements": False,
            },
        )
        if abs(page.get("width", 0) - 28.0) > 0.001:
            raise RuntimeError(f"Layout page did not round-trip: {page}")
        layout_file = server._run_live(
            "save_layout_file",
            {
                "layout_name": layout_name,
                "output_file": str(pagx),
                "overwrite": True,
            },
        )
        if not pagx.exists() or pagx.stat().st_size == 0:
            raise RuntimeError("PAGX output was not created")

        summary["map_properties"] = map_properties
        summary["native_files"] = {
            "mapx": map_file,
            "pagx": layout_file,
            "bkmx": bookmark_export,
        }
        summary["bookmarks"] = {
            "first": first,
            "second": second,
            "configured": configured,
            "moved": moved,
            "exported": bookmark_export,
            "imported": bookmark_import,
        }
        summary["layout_page"] = page
    finally:
        cleanup: dict[str, object] = {}
        if layout_created:
            try:
                cleanup["layout"] = server._run_live(
                    "remove_layout", {"layout_name": layout_name, "confirm_remove": True}
                )
            except Exception as exc:
                cleanup["layout_error"] = str(exc)
        cleanup_maps: list[object] = []
        for map_name in reversed(created_maps):
            try:
                cleanup_maps.append(
                    server._run_live(
                        "remove_map", {"map_name": map_name, "confirm_remove": True}
                    )
                )
            except Exception as exc:
                cleanup_maps.append({"map": map_name, "error": str(exc)})
        cleanup["maps"] = cleanup_maps
        summary["cleanup"] = cleanup

    output = output_dir / "live_v014_validation_latest.json"
    output.write_text(
        json.dumps(summary, ensure_ascii=False, indent=2, default=str), encoding="utf-8"
    )
    print(json.dumps(summary, ensure_ascii=False, indent=2, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
