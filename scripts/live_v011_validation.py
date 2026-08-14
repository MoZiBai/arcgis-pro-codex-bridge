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
    if status.get("protocol_version") != "0.11.1":
        raise RuntimeError(
            f"Expected protocol 0.11.1, got {status.get('protocol_version')}"
        )
    capabilities = server._run_live("capabilities", {})
    actions = set(capabilities.get("actions") or [])
    required = {
        "list_dynamic_frames",
        "add_table_frame",
        "add_chart_frame",
        "list_label_classes",
        "configure_label_class",
        "remove_label_classes",
        "create_report",
        "configure_report",
        "configure_map_grid",
    }
    if len(actions) < 134 or not required.issubset(actions):
        raise RuntimeError(
            f"Expected at least 134 actions including v0.11 additions; got {len(actions)}"
        )

    marker = str(int(time.time()))
    map_name = f"CodexV011Validation_{marker}"
    layout_name = f"CodexV011Layout_{marker}"
    report_name = f"CodexV011Report_{marker}"
    synthetic_gdb = (
        ROOT
        / "outputs"
        / "宜昌市测试数据"
        / "yichang_synthetic_20260730_230117.gdb"
    )
    districts = synthetic_gdb / "模拟区县"
    residents = synthetic_gdb / "模拟居民点"
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
    map_created = False
    layout_created = False
    report_created = False
    try:
        summary["create_map"] = server._run_live(
            "create_map", {"map_name": map_name, "open_map": False}
        )
        map_created = True
        summary["add_synthetic_data"] = server._run_live(
            "add_data",
            {"map_name": map_name, "data_paths": [str(districts), str(residents)]},
        )

        label_a = server._run_live(
            "configure_label_class",
            {
                "map_name": map_name,
                "layer_name": "模拟居民点",
                "label_class_name": "Codex Towns",
                "field": "NAME",
                "where_clause": "POP >= 5000",
                "visible": True,
                "priority": 10,
                "minimum_scale": 0,
                "maximum_scale": 0,
                "text_symbol": {"size": 8, "color": "#27343B"},
                "maplex_placement": {
                    "primary_offset": 2,
                    "prefer_horizontal_placement": True,
                    "label_buffer": 1,
                },
            },
        )
        label_b = server._run_live(
            "configure_label_class",
            {
                "map_name": map_name,
                "layer_name": "模拟居民点",
                "label_class_name": "Codex Villages",
                "field": "NAME",
                "where_clause": "POP < 5000",
                "visible": False,
                "priority": 20,
                "standard_placement": {"allow_overlapping_labels": False},
            },
        )
        labels = server._run_live(
            "list_label_classes",
            {"map_name": map_name, "layer_name": "模拟居民点"},
        )
        names = {item.get("name") for item in labels.get("label_classes", [])}
        if not {"Codex Towns", "Codex Villages"}.issubset(names):
            raise RuntimeError(f"Label classes did not round-trip: {names}")
        removed_label = server._run_live(
            "remove_label_classes",
            {
                "map_name": map_name,
                "layer_name": "模拟居民点",
                "label_class_names": ["Codex Villages"],
                "confirm_remove": True,
            },
        )
        summary["label_classes"] = {
            "first": label_a,
            "second": label_b,
            "inventory": labels,
            "remove": removed_label,
        }

        summary["create_layout"] = server._run_live(
            "create_layout",
            {
                "layout_name": layout_name,
                "map_name": map_name,
                "map_frame_name": "Validation Map Frame",
                "legend": False,
                "north_arrow": False,
                "scale_bar": False,
                "open_layout": False,
            },
        )
        layout_created = True
        table_frame = server._run_live(
            "add_table_frame",
            {
                "layout_name": layout_name,
                "map_frame_name": "Validation Map Frame",
                "map_member_name": "模拟居民点",
                "element_name": "Validation Table",
                "field_names": ["NAME", "TYPE", "POP"],
                "bounds": {"xmin": 1, "ymin": 1, "xmax": 12, "ymax": 6},
                "row_limit": 20,
                "show_headings": True,
            },
        )

        chart_name = "Validation Chart"
        server._run_live(
            "set_charts",
            {
                "map_name": map_name,
                "layer_name": "模拟居民点",
                "charts": [
                    {
                        "type": "CIMChart",
                        "name": chart_name,
                        "chartType": "Basic",
                        "series": [],
                    }
                ],
            },
        )
        chart_frame = server._run_live(
            "add_chart_frame",
            {
                "layout_name": layout_name,
                "map_frame_name": "Validation Map Frame",
                "map_member_name": "模拟居民点",
                "chart_name": chart_name,
                "element_name": "Validation Chart Frame",
                "bounds": {"xmin": 13, "ymin": 1, "xmax": 24, "ymax": 8},
            },
        )
        dynamic_frames = server._run_live(
            "list_dynamic_frames", {"layout_name": layout_name}
        )
        dynamic_types = {item.get("frame_type") for item in dynamic_frames.get("dynamic_frames", [])}
        if not {"TableFrame", "ChartFrame"}.issubset(dynamic_types):
            raise RuntimeError(f"Dynamic frames did not round-trip: {dynamic_types}")
        summary["dynamic_frames"] = {
            "table": table_frame,
            "chart": chart_frame,
            "inventory": dynamic_frames,
        }

        styles = server._run_live(
            "list_style_items", {"item_type": "grid", "search": "", "max_items": 20}
        )
        grid_items = styles.get("items") or []
        if grid_items:
            chosen = grid_items[0]
            added_grid = server._run_live(
                "add_map_grid",
                {
                    "layout_name": layout_name,
                    "map_frame_name": "Validation Map Frame",
                    "style_name": chosen.get("style"),
                    "style_item_name": chosen.get("name"),
                    "grid_name": "Validation Grid",
                },
            )
            configured_grid = server._run_live(
                "configure_map_grid",
                {
                    "layout_name": layout_name,
                    "map_frame_name": "Validation Map Frame",
                    "grid_name": "Validation Grid",
                    "interval": 1,
                    "draw_grid_lines": False,
                    "exterior_ticks_visible": True,
                    "interior_ticks_visible": False,
                },
            )
            summary["map_grid"] = {
                "style": chosen,
                "add": added_grid,
                "configure": configured_grid,
            }
        else:
            summary["map_grid"] = {"skipped": "No referenced grid style was available"}

        summary["create_report"] = server._run_live(
            "create_report",
            {
                "report_name": report_name,
                "map_name": map_name,
                "map_member_name": "模拟居民点",
                "field_names": ["NAME", "TYPE", "POP"],
                "statistics": [{"field": "POP", "statistic": "Sum"}],
                "page_width": 21.0,
                "page_height": 29.7,
                "open_report": False,
            },
        )
        report_created = True
        report_inventory = server._run_live(
            "configure_report", {"report_name": report_name}
        )
        sections = report_inventory.get("sections") or []
        if not sections:
            raise RuntimeError("Created report has no sections")
        first_section = sections[0]
        report_configured = server._run_live(
            "configure_report",
            {
                "report_name": report_name,
                "definition_query": "1=1",
                "sections": [
                    {
                        "name": first_section["name"],
                        "height": first_section["height"],
                        "visible": first_section["visible"],
                    }
                ],
            },
        )
        summary["report"] = {
            "inventory": report_inventory,
            "configured": report_configured,
        }
    finally:
        cleanup: dict[str, object] = {}
        if report_created:
            try:
                cleanup["report"] = server._run_live(
                    "remove_report",
                    {"report_name": report_name, "confirm_remove": True},
                )
            except Exception as exc:
                cleanup["report_error"] = str(exc)
        if layout_created:
            try:
                cleanup["layout"] = server._run_live(
                    "remove_layout",
                    {"layout_name": layout_name, "confirm_remove": True},
                )
            except Exception as exc:
                cleanup["layout_error"] = str(exc)
        if map_created:
            try:
                cleanup["map"] = server._run_live(
                    "remove_map", {"map_name": map_name, "confirm_remove": True}
                )
            except Exception as exc:
                cleanup["map_error"] = str(exc)
        summary["cleanup"] = cleanup

    output = ROOT / "outputs" / "bridge_runtime_tests" / "live_v011_validation_latest.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(
        json.dumps(summary, ensure_ascii=False, indent=2, default=str),
        encoding="utf-8",
    )
    print(json.dumps(summary, ensure_ascii=False, indent=2, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
