from __future__ import annotations

import argparse
import json
import sys
from datetime import datetime
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import ArcGISMCP


PALETTE = [
    "#16324F",
    "#2F6690",
    "#3A7D8C",
    "#86BBD8",
    "#F2CC8F",
    "#E07A5F",
]


def build_commands(layout_name: str, pdf_path: Path, png_path: Path) -> list[dict]:
    return [
        {
            "action": "manage_layers",
            "payload": {
                "all": True,
                "visible": False,
            },
        },
        {
            "action": "manage_layers",
            "payload": {
                "layer_names": ["模拟区县", "模拟居民点"],
                "visible": True,
            },
        },
        {
            "action": "apply_vector_symbology",
            "payload": {
                "layer_name": "模拟区县",
                "renderer": "unique_value",
                "field": "NAME",
                "color_stops": PALETTE,
                "symbol": {
                    "color": "#86BBD8B8",
                    "outline_color": "#F7F4EA",
                    "width": 0.8,
                },
            },
        },
        {
            "action": "apply_vector_symbology",
            "payload": {
                "layer_name": "模拟居民点",
                "renderer": "graduated_colors",
                "field": "POP",
                "classification": "NaturalBreaks",
                "class_count": 5,
                "color_stops": ["#F7EDE2", "#F6BD60", "#F28482", "#B56576", "#6D597A"],
                "symbol": {
                    "color": "#F28482",
                    "size": 5.5,
                    "marker": "Circle",
                },
            },
        },
        {
            "action": "configure_labels",
            "payload": {
                "layer_name": "模拟区县",
                "visible": True,
                "field": "NAME",
                "text_symbol": {
                    "color": "#202830",
                    "size": 8,
                    "font_family": "Microsoft YaHei",
                    "font_style": "Regular",
                },
            },
        },
        {
            "action": "draw_graphics",
            "payload": {
                "graphics_layer_name": "Codex 桥接测试图形",
                "clear_existing": True,
                "graphics": [
                    {
                        "type": "point",
                        "name": "测试点",
                        "coordinates": [111.08, 30.72],
                        "symbol": {"color": "#E63946", "size": 10, "marker": "Diamond"},
                    },
                    {
                        "type": "polyline",
                        "name": "测试线",
                        "coordinates": [[110.52, 30.25], [110.94, 30.72], [111.52, 31.22]],
                        "symbol": {"color": "#1D3557", "width": 2.0, "line_style": "Dash"},
                    },
                    {
                        "type": "polygon",
                        "name": "测试面",
                        "coordinates": [[111.32, 30.30], [111.75, 30.36], [111.70, 30.68], [111.26, 30.62]],
                        "symbol": {
                            "color": "#2A9D8F55",
                            "outline_color": "#2A9D8F",
                            "width": 1.4,
                        },
                    },
                    {
                        "type": "rectangle",
                        "name": "测试矩形",
                        "bounds": {"xmin": 110.42, "ymin": 31.05, "xmax": 110.78, "ymax": 31.34},
                        "symbol": {
                            "color": "#FFB70340",
                            "outline_color": "#FB8500",
                            "width": 1.2,
                        },
                    },
                    {
                        "type": "text",
                        "name": "测试文字",
                        "coordinates": [111.18, 31.37],
                        "text": "SDK 矢量图形",
                        "text_symbol": {
                            "color": "#7A1E48",
                            "size": 10,
                            "font_family": "Microsoft YaHei",
                            "font_style": "Bold",
                        },
                    },
                ],
            },
        },
        {
            "action": "reorder_layer",
            "payload": {"layer_name": "Codex 桥接测试图形", "position": "top"},
        },
        {
            "action": "zoom_to_layer",
            "payload": {"layer_name": "模拟区县", "scale_factor": 1.08},
        },
        {
            "action": "create_layout",
            "payload": {
                "layout_name": layout_name,
                "page_width": 29.7,
                "page_height": 21.0,
                "title": "桥接测试",
                "title_x": 14.85,
                "title_y": 20.35,
                "title_symbol": {
                    "color": "#1F2933",
                    "size": 18,
                    "font_family": "Microsoft YaHei",
                    "font_style": "Bold",
                },
                "map_frame_name": "主图",
                "map_frame_bounds": {"xmin": 1.6, "ymin": 1.85, "xmax": 28.1, "ymax": 19.3},
                "map_frame_border": True,
                "map_frame_border_color": "#263238",
                "map_frame_border_width": 0.8,
                "extent_layer_name": "模拟区县",
                "scale_factor": 1.08,
                "legend": {
                    "enabled": True,
                    "bounds": {"xmin": 22.5, "ymin": 2.65, "xmax": 27.45, "ymax": 7.3},
                    "title": "图例",
                    "show_title": True,
                    "layer_names": ["模拟区县", "模拟居民点"],
                    "background": True,
                    "background_color": "#FFFFFFE8",
                    "border_color": "#64748B",
                    "border_width": 0.45,
                },
                "north_arrow": {
                    "enabled": True,
                    "bounds": {"xmin": 25.0, "ymin": 15.8, "xmax": 27.4, "ymax": 18.35},
                },
                "scale_bar": {
                    "enabled": True,
                    "bounds": {"xmin": 11.1, "ymin": 0.42, "xmax": 18.6, "ymax": 1.3},
                    "units": "Kilometers",
                    "unit_label": "km",
                    "divisions": 4,
                    "subdivisions": 2,
                },
                "ticks": {
                    "enabled": True,
                    "x_intervals": 5,
                    "y_intervals": 5,
                    "tick_length": 0.12,
                    "label_offset": 0.16,
                    "color": "#263238",
                    "width": 0.55,
                    "text_symbol": {
                        "color": "#263238",
                        "size": 6.5,
                        "font_family": "Arial",
                    },
                },
                "colorbar": {
                    "enabled": True,
                    "bounds": {"xmin": 2.15, "ymin": 2.55, "xmax": 2.8, "ymax": 6.8},
                    "orientation": "vertical",
                    "color_stops": PALETTE,
                    "minimum": 0,
                    "maximum": 100,
                    "title": "测试色带",
                    "text_symbol": {
                        "color": "#263238",
                        "size": 6.5,
                        "font_family": "Microsoft YaHei",
                    },
                },
                "open_layout": True,
            },
        },
        {
            "action": "add_layout_graphics",
            "payload": {
                "layout_name": layout_name,
                "graphics": [
                    {
                        "type": "text",
                        "name": "测试说明",
                        "position": [1.8, 0.7],
                        "text": "不保存当前项目 · 不修改源数据",
                        "text_symbol": {
                            "color": "#475569",
                            "size": 7,
                            "font_family": "Microsoft YaHei",
                        },
                    },
                    {
                        "type": "line",
                        "name": "标题分隔线",
                        "coordinates": [[9.6, 19.85], [20.1, 19.85]],
                        "color": "#94A3B8",
                        "width": 0.65,
                    },
                ],
            },
        },
        {
            "action": "set_layout_text",
            "payload": {
                "layout_name": layout_name,
                "element_name": "标题",
                "text": "ArcGIS Codex 桥接 v0.3 运行测试",
            },
        },
        {
            "action": "update_layout_element",
            "payload": {
                "layout_name": layout_name,
                "element_name": "标题",
                "x": 14.85,
                "y": 20.35,
                "rotation": 0,
                "visible": True,
            },
        },
        {
            "action": "zoom_layout",
            "payload": {
                "layout_name": layout_name,
                "map_frame_name": "主图",
                "layer_name": "模拟区县",
                "scale_factor": 1.08,
            },
        },
        {
            "action": "export_layout",
            "payload": {
                "layout_name": layout_name,
                "output_file": str(pdf_path),
                "dpi": 240,
            },
        },
        {
            "action": "export_layout",
            "payload": {
                "layout_name": layout_name,
                "output_file": str(png_path),
                "dpi": 180,
            },
        },
    ]


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Run a non-destructive ArcGIS Pro live bridge v0.3 integration test."
    )
    parser.add_argument("--config", type=Path, default=ROOT / "config.json")
    parser.add_argument("--layout-name")
    args = parser.parse_args()

    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    layout_name = args.layout_name or f"桥接能力测试_{stamp}"
    output_dir = ROOT / "outputs" / "bridge_runtime_tests"
    output_dir.mkdir(parents=True, exist_ok=True)
    pdf_path = output_dir / f"{layout_name}.pdf"
    png_path = output_dir / f"{layout_name}.png"

    server = ArcGISMCP(args.config)
    capabilities = server.call_tool("live_capabilities", {})
    status_before = server.call_tool("live_bridge_status", {})
    batch = server.call_tool(
        "live_execute_batch",
        {
            "commands": build_commands(layout_name, pdf_path, png_path),
            "continue_on_error": False,
        },
    )
    status_after = server.call_tool("live_bridge_status", {})

    report = {
        "safety": {
            "source_data_modified": False,
            "project_saved": False,
            "output_root_only": True,
        },
        "layout_name": layout_name,
        "pdf": str(pdf_path),
        "png": str(png_path),
        "capabilities": capabilities,
        "status_before": status_before,
        "batch": batch,
        "status_after": status_after,
    }
    report_path = output_dir / f"{layout_name}.json"
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
