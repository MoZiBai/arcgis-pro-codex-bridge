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
    status = server._run_live("status", {})
    if status.get("protocol_version") != "0.9.0":
        raise RuntimeError(f"Expected protocol 0.9.0, got {status.get('protocol_version')}")
    capabilities = server._run_live("capabilities", {})
    actions = set(capabilities.get("actions") or [])
    if len(actions) < 120:
        raise RuntimeError(f"Expected at least 120 actions, got {len(actions)}")

    map_name = status.get("active_map") or (status.get("maps") or [None])[0]
    if not map_name:
        raise RuntimeError("The current project has no map for temporary layout validation.")

    marker = str(int(time.time()))
    layout_name = f"CodexCartographyValidation_{marker}"
    frame_name = "Validation Map Frame"
    summary: dict[str, object] = {
        "handshake": {
            "addin_version": status.get("addin_version"),
            "protocol_version": status.get("protocol_version"),
            "action_count": len(actions),
        }
    }
    layout_created = False
    try:
        grid_styles = server._run_live(
            "list_style_items",
            {"item_type": "grid", "search": "", "max_items": 100},
        )
        legend_styles = server._run_live(
            "list_style_items",
            {"item_type": "legend", "search": "", "max_items": 20},
        )
        north_styles = server._run_live(
            "list_style_items",
            {"item_type": "north_arrow", "search": "", "max_items": 20},
        )
        scale_styles = server._run_live(
            "list_style_items",
            {"item_type": "scale_bar", "search": "", "max_items": 20},
        )
        summary["style_counts"] = {
            "grid": grid_styles.get("returned"),
            "legend": legend_styles.get("returned"),
            "north_arrow": north_styles.get("returned"),
            "scale_bar": scale_styles.get("returned"),
        }

        created = server._run_live(
            "create_layout",
            {
                "layout_name": layout_name,
                "map_name": map_name,
                "map_frame_name": frame_name,
                "page_width": 11,
                "page_height": 8.5,
                "map_frame_bounds": {"xmin": 0.7, "ymin": 0.8, "xmax": 10.3, "ymax": 7.8},
                "map_frame_border": True,
                "legend": False,
                "north_arrow": False,
                "scale_bar": False,
                "open_layout": False,
            },
        )
        layout_created = True
        summary["layout"] = created

        grid_items = grid_styles.get("items") or []
        if grid_items:
            selected = grid_items[0]
            added_grid = server._run_live(
                "add_map_grid",
                {
                    "layout_name": layout_name,
                    "map_frame_name": frame_name,
                    "style_name": selected.get("style"),
                    "style_item_name": selected.get("name"),
                    "grid_name": "Validation Grid",
                },
            )
            grids = server._run_live(
                "list_map_grids",
                {
                    "layout_name": layout_name,
                    "map_frame_name": frame_name,
                    "include_cim": True,
                },
            )
            grid = next(
                item for item in grids.get("grids", [])
                if item.get("name") == "Validation Grid"
            )
            grid_type = str(grid.get("grid_type", "")).lower()
            roundtrip = None
            if grid_type == "cimgraticule":
                roundtrip = server._run_live(
                    "set_map_grid_cim",
                    {
                        "layout_name": layout_name,
                        "map_frame_name": frame_name,
                        "grid_name": "Validation Grid",
                        "grid_type": "graticule",
                        "cim_json": grid["cim_json"],
                        "confirm_replace_definition": True,
                    },
                )
            elif grid_type == "cimmeasuredgrid":
                roundtrip = server._run_live(
                    "set_map_grid_cim",
                    {
                        "layout_name": layout_name,
                        "map_frame_name": frame_name,
                        "grid_name": "Validation Grid",
                        "grid_type": "measured_grid",
                        "cim_json": grid["cim_json"],
                        "confirm_replace_definition": True,
                    },
                )
            removed_grid = server._run_live(
                "remove_map_grids",
                {
                    "layout_name": layout_name,
                    "map_frame_name": frame_name,
                    "grid_names": ["Validation Grid"],
                    "confirm_remove": True,
                },
            )
            summary["grid"] = {
                "selected_style": selected,
                "added": added_grid,
                "listed": grid,
                "cim_roundtrip": roundtrip,
                "removed": removed_grid,
            }
        else:
            summary["grid"] = {"skipped": "No referenced grid style items were found."}

        surround_specs = [
            (
                "legend",
                "Validation Legend",
                {"xmin": 7.2, "ymin": 1.2, "xmax": 10.0, "ymax": 4.0},
                legend_styles,
            ),
            (
                "north_arrow",
                "Validation North Arrow",
                {"xmin": 8.8, "ymin": 6.1, "xmax": 10.0, "ymax": 7.3},
                north_styles,
            ),
            (
                "scale_bar",
                "Validation Scale Bar",
                {"xmin": 3.2, "ymin": 1.0, "xmax": 6.8, "ymax": 1.6},
                scale_styles,
            ),
        ]
        surrounds_created = []
        for surround_type, element_name, bounds, styles in surround_specs:
            items = styles.get("items") or []
            payload = {
                "layout_name": layout_name,
                "map_frame_name": frame_name,
                "surround_type": surround_type,
                "element_name": element_name,
                "bounds": bounds,
            }
            if items:
                payload["style_name"] = items[0].get("style")
                payload["style_item_name"] = items[0].get("name")
            if surround_type == "legend":
                payload.update({"title": "Validation Legend", "show_title": True})
            if surround_type == "scale_bar":
                payload.update(
                    {
                        "units": "kilometers",
                        "unit_label": "km",
                        "divisions": 4,
                        "subdivisions": 2,
                        "division_value": 50,
                    }
                )
            surrounds_created.append(server._run_live("add_map_surround", payload))

        configured = server._run_live(
            "configure_map_surround",
            {
                "layout_name": layout_name,
                "element_name": "Validation Legend",
                "x": 7.0,
                "y": 1.1,
                "width": 2.7,
                "height": 2.6,
                "visible": True,
                "locked": False,
                "title": "Updated Validation Legend",
                "show_title": True,
                "auto_add": True,
            },
        )
        surrounds = server._run_live(
            "list_map_surrounds",
            {"layout_name": layout_name, "include_cim": True},
        )
        legend = next(
            item for item in surrounds.get("surrounds", [])
            if item.get("name") == "Validation Legend"
        )
        cim_roundtrip = server._run_live(
            "set_map_surround_cim",
            {
                "layout_name": layout_name,
                "element_name": "Validation Legend",
                "surround_type": "legend",
                "cim_json": legend["cim_json"],
                "confirm_replace_definition": True,
            },
        )
        summary["surrounds"] = {
            "created": surrounds_created,
            "configured": configured,
            "listed_count": len(surrounds.get("surrounds", [])),
            "legend_cim_roundtrip": cim_roundtrip,
        }
    finally:
        if layout_created:
            summary["cleanup"] = server._run_live(
                "remove_layout",
                {"layout_name": layout_name, "confirm_remove": True},
            )

    print(json.dumps(summary, ensure_ascii=False, indent=2, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
