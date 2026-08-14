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
    if status.get("protocol_version") not in {"0.18.0", "0.19.0", "0.20.0", "0.21.0", "0.22.0", "0.23.0", "0.23.1", "0.23.2", "0.24.0", "0.25.0", "0.26.0", "0.27.0", "0.27.1", "0.27.2", "0.27.3", "0.28.0", "0.28.1", "0.28.2"}:
        raise RuntimeError(f"Expected protocol 0.18.0 or compatible 0.19/0.20, got {status.get('protocol_version')}")
    actions = set(status.get("capabilities") or [])
    required = {
        "get_layout_element_cim",
        "set_layout_element_cim",
        "create_layout_element_from_cim",
        "get_standalone_table_cim",
        "set_standalone_table_cim",
        "get_report_cim",
        "set_report_cim",
        "get_report_element_cim",
        "set_report_element_cim",
        "get_presentation_cim",
        "set_presentation_cim",
        "get_presentation_page_cim",
        "set_presentation_page_cim",
        "get_presentation_page_element_cim",
        "set_presentation_page_element_cim",
    }
    if len(actions) < 174 or not required.issubset(actions):
        raise RuntimeError(
            f"Expected 174 actions including all v0.18 additions; got {len(actions)}"
        )

    vector_root = ROOT / "outputs" / "bridge_runtime_tests" / "vector_inputs_v015"
    table_root = ROOT / "outputs" / "bridge_runtime_tests" / "table_inputs_v018"
    manifest = json.loads((vector_root / "manifest.json").read_text(encoding="utf-8"))
    table_manifest = json.loads((table_root / "manifest.json").read_text(encoding="utf-8"))
    residents = Path(manifest["residents"])
    standalone_table = Path(table_manifest["table"])

    marker = str(int(time.time()))
    map_name = f"CodexV018Cim_{marker}"
    table_name = f"CodexV018Table_{marker}"
    layout_name = f"CodexV018Layout_{marker}"
    report_name = f"CodexV018Report_{marker}"
    presentation_name = f"CodexV018Presentation_{marker}"
    cloned_element_name = f"CodexV018Element_{marker}"
    project_path = Path(str(status["project"]))
    project_before = project_path.stat()
    vector_before = fingerprint_tree(vector_root)
    table_before = fingerprint_tree(table_root)
    summary: dict[str, object] = {
        "handshake": {
            "addin_version": status.get("addin_version"),
            "protocol_version": status.get("protocol_version"),
            "action_count": len(actions),
        },
        "source_data_modified": False,
        "project_saved": False,
    }
    created = {
        "map": False,
        "layout": False,
        "report": False,
        "presentation": False,
    }
    try:
        server._run_live("create_map", {"map_name": map_name, "open_map": False})
        created["map"] = True
        server._run_live("add_data", {"map_name": map_name, "data_paths": [str(residents)]})
        server._run_live(
            "add_standalone_table",
            {"map_name": map_name, "table_path": str(standalone_table), "table_name": table_name},
        )

        table_cim = server._run_live(
            "get_standalone_table_cim",
            {"map_name": map_name, "table_name": table_name},
        )
        table_round_trip = server._run_live(
            "set_standalone_table_cim",
            {
                "map_name": map_name,
                "table_name": table_name,
                "cim_json": table_cim["cim_json"],
                "confirm_replace_definition": True,
            },
        )
        if table_round_trip.get("source_connection_preserved") is not True:
            raise RuntimeError(f"Standalone-table connection was not preserved: {table_round_trip}")

        server._run_live(
            "create_layout",
            {
                "layout_name": layout_name,
                "map_name": map_name,
                "title": "v0.18 temporary CIM validation",
                "legend": False,
                "north_arrow": False,
                "scale_bar": False,
                "open_layout": False,
            },
        )
        created["layout"] = True
        layout_element = server._run_live(
            "get_layout_element_cim",
            {"layout_name": layout_name, "element_name": "标题"},
        )
        layout_round_trip = server._run_live(
            "set_layout_element_cim",
            {
                "layout_name": layout_name,
                "element_name": "标题",
                "cim_json": layout_element["cim_json"],
                "confirm_replace_definition": True,
            },
        )
        created_element = server._run_live(
            "create_layout_element_from_cim",
            {
                "layout_name": layout_name,
                "cim_type": layout_element["cim_type"],
                "cim_json": layout_element["cim_json"],
                "element_name": cloned_element_name,
            },
        )
        if created_element.get("created") is not True:
            raise RuntimeError(f"Layout CIM element was not created: {created_element}")

        server._run_live(
            "create_report",
            {
                "report_name": report_name,
                "map_name": map_name,
                "map_member_name": "validation_residents",
                "open_report": False,
            },
        )
        created["report"] = True
        report_cim = server._run_live("get_report_cim", {"report_name": report_name})
        report_round_trip = server._run_live(
            "set_report_cim",
            {
                "report_name": report_name,
                "cim_json": report_cim["cim_json"],
                "confirm_replace_definition": True,
            },
        )
        report_inventory = server._run_live("list_reports", {})
        report_entry = next(
            item for item in report_inventory["reports"] if item["name"] == report_name
        )
        report_elements = report_entry.get("elements") or []
        if not report_elements:
            raise RuntimeError("Temporary report exposed no report elements")
        report_element_name = report_elements[0]["name"]
        report_element = server._run_live(
            "get_report_element_cim",
            {"report_name": report_name, "element_name": report_element_name},
        )
        report_element_round_trip = server._run_live(
            "set_report_element_cim",
            {
                "report_name": report_name,
                "element_name": report_element_name,
                "cim_json": report_element["cim_json"],
                "confirm_replace_definition": True,
            },
        )

        server._run_live(
            "create_presentation",
            {"presentation_name": presentation_name, "aspect_width": 16, "aspect_height": 9},
        )
        created["presentation"] = True
        server._run_live(
            "add_presentation_page",
            {
                "presentation_name": presentation_name,
                "page_type": "blank",
                "blank_template": "TitleAndParagraph",
                "insert_index": 0,
            },
        )
        presentation_cim = server._run_live(
            "get_presentation_cim", {"presentation_name": presentation_name}
        )
        presentation_round_trip = server._run_live(
            "set_presentation_cim",
            {
                "presentation_name": presentation_name,
                "cim_json": presentation_cim["cim_json"],
                "confirm_replace_definition": True,
            },
        )
        page_cim = server._run_live(
            "get_presentation_page_cim",
            {"presentation_name": presentation_name, "page_index": 0},
        )
        page_round_trip = server._run_live(
            "set_presentation_page_cim",
            {
                "presentation_name": presentation_name,
                "page_index": 0,
                "cim_json": page_cim["cim_json"],
                "confirm_replace_definition": True,
            },
        )
        presentation_inventory = server._run_live("list_presentations", {})
        presentation_entry = next(
            item
            for item in presentation_inventory["presentations"]
            if item["name"] == presentation_name
        )
        page_elements = presentation_entry["pages"][0].get("elements") or []
        page_element_round_trip = None
        if page_elements:
            page_element_name = page_elements[0]["name"]
            page_element = server._run_live(
                "get_presentation_page_element_cim",
                {
                    "presentation_name": presentation_name,
                    "page_index": 0,
                    "element_name": page_element_name,
                },
            )
            page_element_round_trip = server._run_live(
                "set_presentation_page_element_cim",
                {
                    "presentation_name": presentation_name,
                    "page_index": 0,
                    "element_name": page_element_name,
                    "cim_json": page_element["cim_json"],
                    "confirm_replace_definition": True,
                },
            )

        summary["standalone_table"] = table_round_trip
        summary["layout_element"] = {
            "round_trip": layout_round_trip,
            "created": created_element,
        }
        summary["report"] = {
            "round_trip": report_round_trip,
            "element_round_trip": report_element_round_trip,
        }
        summary["presentation"] = {
            "round_trip": presentation_round_trip,
            "page_round_trip": page_round_trip,
            "element_round_trip": page_element_round_trip,
        }
    finally:
        cleanup: list[dict[str, object]] = []
        targets = [
            ("presentation", "remove_presentation", {"presentation_name": presentation_name, "confirm_remove": True}),
            ("report", "remove_report", {"report_name": report_name, "confirm_remove": True}),
            ("layout", "remove_layout", {"layout_name": layout_name, "confirm_remove": True}),
            ("map", "remove_map", {"map_name": map_name, "confirm_remove": True}),
        ]
        for key, action, payload in targets:
            if not created[key]:
                continue
            try:
                cleanup.append({"target": key, "result": server._run_live(action, payload)})
            except Exception as exc:
                cleanup.append({"target": key, "error": str(exc)})
        summary["cleanup"] = cleanup

    project_after = project_path.stat()
    summary["project_file_unchanged"] = (
        project_before.st_size == project_after.st_size
        and project_before.st_mtime_ns == project_after.st_mtime_ns
    )
    summary["synthetic_source_fingerprint_unchanged"] = (
        vector_before == fingerprint_tree(vector_root)
        and table_before == fingerprint_tree(table_root)
    )
    if not summary["project_file_unchanged"]:
        raise RuntimeError("The open .aprx file changed during v0.18 validation")
    if not summary["synthetic_source_fingerprint_unchanged"]:
        raise RuntimeError("Synthetic validation inputs changed during v0.18 validation")

    output = ROOT / "outputs" / "bridge_runtime_tests" / "live_v018_validation_latest.json"
    output.write_text(json.dumps(summary, ensure_ascii=False, indent=2, default=str), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
