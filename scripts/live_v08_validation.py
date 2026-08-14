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
    marker = str(int(time.time()))
    presentation_name = f"CodexBridgeValidation_{marker}"
    catalog_folder = ROOT / "outputs" / "bridge_validation_catalog"
    catalog_folder.mkdir(parents=True, exist_ok=True)

    summary: dict[str, object] = {}
    presentation_created = False
    catalog_added = False
    try:
        status = server._run_live("status", {})
        if status.get("protocol_version") != "0.8.0":
            raise RuntimeError(f"Expected protocol 0.8.0, got {status.get('protocol_version')}")
        capabilities = server._run_live("capabilities", {})
        actions = set(capabilities.get("actions") or [])
        if len(actions) < 111:
            raise RuntimeError(f"Expected at least 111 actions, got {len(actions)}")
        summary["handshake"] = {
            "addin_version": status.get("addin_version"),
            "protocol_version": status.get("protocol_version"),
            "action_count": len(actions),
        }

        before_catalog = server._run_live("list_catalog_items", {"max_items": 5000})
        added = server._run_live(
            "add_project_item",
            {"path": str(catalog_folder), "alias": "Codex bridge validation"},
        )
        catalog_added = bool(added.get("added"))
        after_catalog = server._run_live("list_catalog_items", {"max_items": 5000})
        summary["catalog"] = {
            "before_count": before_catalog.get("total_project_items"),
            "added": added,
            "after_count": after_catalog.get("total_project_items"),
        }

        created = server._run_live(
            "create_presentation",
            {
                "presentation_name": presentation_name,
                "aspect_width": 16,
                "aspect_height": 9,
            },
        )
        presentation_created = True
        page = server._run_live(
            "add_presentation_page",
            {
                "presentation_name": presentation_name,
                "page_type": "blank",
                "blank_template": "TitleAndParagraph",
                "background_color": [248, 249, 250, 100],
                "hold_time": 2.5,
            },
        )
        configured_page = server._run_live(
            "configure_presentation_page",
            {
                "presentation_name": presentation_name,
                "page_index": 0,
                "visible": True,
                "locked": False,
                "automatic_advancement": False,
            },
        )
        configured = server._run_live(
            "configure_presentation",
            {
                "presentation_name": presentation_name,
                "aspect_width": 4,
                "aspect_height": 3,
            },
        )
        listed = server._run_live("list_presentations", {})
        matching = [
            item
            for item in listed.get("presentations", [])
            if item.get("name") == presentation_name
        ]
        if len(matching) != 1 or matching[0].get("page_count") != 1:
            raise RuntimeError("Temporary presentation did not round-trip through list_presentations")
        summary["presentation"] = {
            "created": created,
            "page": page,
            "configured_page": configured_page,
            "configured": configured,
            "listed": matching[0],
        }
    finally:
        if presentation_created:
            summary["presentation_cleanup"] = server._run_live(
                "remove_presentation",
                {
                    "presentation_name": presentation_name,
                    "confirm_remove": True,
                },
            )
        if catalog_added:
            summary["catalog_cleanup"] = server._run_live(
                "remove_project_item",
                {
                    "path": str(catalog_folder),
                    "confirm_remove": True,
                },
            )

    print(json.dumps(summary, ensure_ascii=False, indent=2, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
