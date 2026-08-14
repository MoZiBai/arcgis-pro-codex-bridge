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
    if status.get("protocol_version") not in {"0.13.0", "0.14.0", "0.14.1", "0.15.0", "0.16.0", "0.17.0", "0.18.0", "0.19.0", "0.20.0", "0.21.0", "0.22.0", "0.23.0", "0.23.1", "0.23.2", "0.24.0", "0.25.0", "0.26.0", "0.27.0", "0.27.1", "0.27.2", "0.27.3", "0.28.0", "0.28.1"}:
        raise RuntimeError(
            f"Expected protocol 0.13.0 or compatible 0.14/0.15, got {status.get('protocol_version')}"
        )
    capabilities = server._run_live("capabilities", {})
    actions = set(capabilities.get("actions") or [])
    required = {
        "list_extent_indicators",
        "add_extent_indicator",
        "configure_extent_indicator",
        "remove_extent_indicators",
        "configure_map_clipping",
        "add_dynamic_text",
        "list_map_series",
        "configure_map_series",
        "remove_map_series",
        "export_map_series",
        "arrange_layout_elements",
        "export_map_view",
    }
    if len(actions) < 146 or not required.issubset(actions):
        raise RuntimeError(
            f"Expected at least 146 actions including v0.12/v0.13 additions; got {len(actions)}"
        )

    marker = str(int(time.time()))
    overview_map = f"CodexV013Overview_{marker}"
    detail_map = f"CodexV013Detail_{marker}"
    layout_name = f"CodexV013Layout_{marker}"
    output_dir = ROOT / "outputs" / "bridge_runtime_tests"
    output_dir.mkdir(parents=True, exist_ok=True)
    map_view_export = output_dir / "live_v013_map_view_latest.png"
    series_export = output_dir / "live_v013_map_series_latest.pdf"
    synthetic_gdb = (
        ROOT
        / "outputs"
        / "bridge_runtime_tests"
        / "vector_inputs_v015"
        / "vector_inputs_v015.gdb"
    )
    districts = synthetic_gdb / "validation_districts"
    residents = synthetic_gdb / "validation_residents"
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
        for map_name in (overview_map, detail_map):
            server._run_live("create_map", {"map_name": map_name, "open_map": False})
            created_maps.append(map_name)
            server._run_live(
                "add_data",
                {
                    "map_name": map_name,
                    "data_paths": [str(districts), str(residents)],
                },
            )

        clipping_enabled = server._run_live(
            "configure_map_clipping",
            {
                "map_name": detail_map,
                "mode": "CustomShape",
                "clipping_layer_name": "validation_districts",
                "excluded_layer_names": ["validation_residents"],
                "border_visible": True,
                "border_color": "#B52A2A",
                "border_width": 1.2,
            },
        )
        if clipping_enabled.get("clipping_mode") != "CustomShape":
            raise RuntimeError(f"Custom clipping did not apply: {clipping_enabled}")

        server._run_live(
            "create_layout",
            {
                "layout_name": layout_name,
                "map_name": overview_map,
                "map_frame_name": "Overview Frame",
                "legend": False,
                "north_arrow": False,
                "scale_bar": False,
                "open_layout": False,
            },
        )
        layout_created = True
        server._run_live(
            "add_map_frame",
            {
                "layout_name": layout_name,
                "map_name": detail_map,
                "map_frame_name": "Detail Frame",
                "bounds": {"xmin": 18, "ymin": 2, "xmax": 28, "ymax": 10},
                "extent_layer_name": "validation_districts",
            },
        )
        indicator_added = server._run_live(
            "add_extent_indicator",
            {
                "layout_name": layout_name,
                "map_frame_name": "Overview Frame",
                "source_map_frame_name": "Detail Frame",
                "indicator_name": "Validation Extent",
                "frame_color": "#C3362B",
                "frame_width": 1.5,
            },
        )
        indicator_configured = server._run_live(
            "configure_extent_indicator",
            {
                "layout_name": layout_name,
                "map_frame_name": "Overview Frame",
                "indicator_name": "Validation Extent",
                "new_name": "Validation Extent Updated",
                "avoid_label_conflict": True,
                "leader_type": "LineToMidpoint",
            },
        )
        indicator_inventory = server._run_live(
            "list_extent_indicators",
            {"layout_name": layout_name, "map_frame_name": "Overview Frame"},
        )
        indicator_names = {
            item.get("name")
            for item in indicator_inventory.get("extent_indicators", [])
        }
        if "Validation Extent Updated" not in indicator_names:
            raise RuntimeError(f"Extent indicator did not round-trip: {indicator_names}")

        text_elements = []
        for index, (name, x) in enumerate(
            (("Validation Text A", 2.0), ("Validation Text B", 6.0), ("Validation Text C", 11.0))
        ):
            text_elements.append(
                server._run_live(
                    "add_dynamic_text",
                    {
                        "layout_name": layout_name,
                        "element_name": name,
                        "text": (
                            "Generated: <dyn type=\"date\" format=\"yyyy-MM-dd\"/>"
                            if index == 0
                            else name
                        ),
                        "x": x,
                        "y": 1.0 + index * 0.3,
                        "text_symbol": {"size": 8, "color": "#29343A"},
                    },
                )
            )
        arranged_align = server._run_live(
            "arrange_layout_elements",
            {
                "layout_name": layout_name,
                "element_names": ["Validation Text A", "Validation Text B", "Validation Text C"],
                "reference_element_name": "Validation Text A",
                "operation": "align_bottom",
            },
        )
        arranged_distribute = server._run_live(
            "arrange_layout_elements",
            {
                "layout_name": layout_name,
                "element_names": ["Validation Text A", "Validation Text B", "Validation Text C"],
                "operation": "distribute_horizontal",
            },
        )

        series_created = server._run_live(
            "create_map_series",
            {
                "layout_name": layout_name,
                "map_frame_name": "Overview Frame",
                "series_type": "spatial",
                "index_layer_name": "validation_districts",
                "name_field": "NAME",
                "enabled": True,
            },
        )
        series_configured = server._run_live(
            "configure_map_series",
            {
                "layout_name": layout_name,
                "starting_page_number": 1,
                "page_name_field": "NAME",
                "sort_field": "NAME",
                "sort_ascending": True,
                "extent_options": "BestFit",
                "margin": 5,
                "margin_type": "Percent",
                "scale_rounding": 100,
            },
        )
        series_inventory = server._run_live(
            "list_map_series", {"layout_name": layout_name}
        )
        listed = series_inventory.get("map_series") or []
        if not listed or not listed[0].get("present") or listed[0].get("page_count", 0) < 1:
            raise RuntimeError(f"Map series did not round-trip: {series_inventory}")
        series_exported = server._run_live(
            "export_map_series",
            {
                "layout_name": layout_name,
                "output_file": str(series_export),
                "dpi": 96,
                "export_pages": "Current",
                "file_options": "ExportAsSinglePDF",
            },
        )
        if not series_export.exists() or series_export.stat().st_size == 0:
            raise RuntimeError("Map-series export file was not created")
        series_removed = server._run_live(
            "remove_map_series",
            {"layout_name": layout_name, "confirm_remove": True},
        )

        server._run_live("open_map", {"map_name": overview_map})
        map_view_exported = server._run_live(
            "export_map_view",
            {
                "map_name": overview_map,
                "output_file": str(map_view_export),
                "dpi": 96,
            },
        )
        if not map_view_export.exists() or map_view_export.stat().st_size == 0:
            raise RuntimeError("Map-view export file was not created")

        indicator_removed = server._run_live(
            "remove_extent_indicators",
            {
                "layout_name": layout_name,
                "map_frame_name": "Overview Frame",
                "indicator_names": ["Validation Extent Updated"],
                "confirm_remove": True,
            },
        )
        clipping_disabled = server._run_live(
            "configure_map_clipping",
            {
                "map_name": detail_map,
                "mode": "None",
                "border_visible": False,
                "clear_custom_shape": True,
                "clear_exclusions": True,
            },
        )

        summary["map_clipping"] = {"enabled": clipping_enabled, "disabled": clipping_disabled}
        summary["extent_indicator"] = {
            "added": indicator_added,
            "configured": indicator_configured,
            "inventory": indicator_inventory,
            "removed": indicator_removed,
        }
        summary["dynamic_text_and_arrangement"] = {
            "texts": text_elements,
            "align": arranged_align,
            "distribute": arranged_distribute,
        }
        summary["map_series"] = {
            "created": series_created,
            "configured": series_configured,
            "inventory": series_inventory,
            "exported": series_exported,
            "removed": series_removed,
        }
        summary["map_view_export"] = map_view_exported
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

    output = output_dir / "live_v013_validation_latest.json"
    output.write_text(
        json.dumps(summary, ensure_ascii=False, indent=2, default=str), encoding="utf-8"
    )
    print(json.dumps(summary, ensure_ascii=False, indent=2, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
