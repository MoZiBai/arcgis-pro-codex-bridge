from __future__ import annotations

import copy
import json
import sys
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import ArcGISMCP


class FakeLiveBridge:
    def __init__(self) -> None:
        self.document: dict[str, Any] = {
            "type": "CIMFeatureLayer",
            "name": "Synthetic layer",
            "visibility": True,
            "renderer": {
                "heading": "Old heading",
                "symbolLayers": [{"type": "CIMSolidFill", "width": 1}],
            },
            "uRI": "cim://preserved",
        }
        self.calls: list[tuple[str, dict[str, Any]]] = []

    def run(self, action: str, payload: dict[str, Any]) -> dict[str, Any]:
        self.calls.append((action, copy.deepcopy(payload)))
        if action in {"get_layer_cim", "get_map_graphic_cim"}:
            return {
                "cim_type": "CIMFeatureLayer",
                "cim_json": json.dumps(self.document),
            }
        if action in {"set_layer_cim", "set_map_graphic_cim"}:
            self.document = json.loads(payload["cim_json"])
            return {
                "cim_type": "CIMFeatureLayer",
                "cim_json": json.dumps(self.document),
                "definition_replaced": True,
                "source_data_modified": False,
            }
        raise AssertionError(f"Unexpected live action: {action}")


def expect_error(error_type: type[BaseException], callback: Any, text: str) -> None:
    try:
        callback()
    except error_type as exc:
        assert text in str(exc), exc
    else:
        raise AssertionError(f"Expected {error_type.__name__}")


def make_server(fake: FakeLiveBridge) -> ArcGISMCP:
    server = object.__new__(ArcGISMCP)
    server._run_live = fake.run  # type: ignore[method-assign]
    return server


def main() -> int:
    original = {
        "a": 1,
        "nested": {"keep": True, "replace": "old", "remove": 5},
        "array": [1, 2],
    }
    snapshot = copy.deepcopy(original)
    merged = ArcGISMCP._merge_cim_patch(
        original,
        {
            "nested": {"replace": "new", "remove": None, "added": 8},
            "array": [3],
        },
    )
    assert original == snapshot, "merge patch mutated the source document"
    assert merged == {
        "a": 1,
        "nested": {"keep": True, "replace": "new", "added": 8},
        "array": [3],
    }
    assert ArcGISMCP._cim_changed_paths(original, merged) == [
        "$.array",
        "$.nested.added",
        "$.nested.remove",
        "$.nested.replace",
    ]

    protected = ArcGISMCP._protected_cim_patch_paths(
        {
            "renderer": {"heading": "safe"},
            "DATA-CONNECTION": {"workspaceConnectionString": "blocked"},
            "pages": [],
        }
    )
    assert protected == ["$.DATA-CONNECTION", "$.pages"]

    fake = FakeLiveBridge()
    server = make_server(fake)
    dry_run = server._patch_cim_object(
        {
            "target_type": "layer",
            "map_name": "Synthetic map",
            "layer_name": "Synthetic layer",
            "patch": {"renderer": {"heading": "New heading"}},
            "dry_run": True,
            "include_cim_json": True,
        }
    )
    assert dry_run["changed"] is True
    assert dry_run["changed_paths"] == ["$.renderer.heading"]
    assert json.loads(dry_run["cim_json"])["renderer"]["heading"] == "New heading"
    assert [action for action, _ in fake.calls] == ["get_layer_cim"]

    expect_error(
        PermissionError,
        lambda: server._patch_cim_object(
            {
                "target_type": "layer",
                "map_name": "Synthetic map",
                "layer_name": "Synthetic layer",
                "patch": {"visibility": False},
            }
        ),
        "confirm_apply=true",
    )
    expect_error(
        PermissionError,
        lambda: server._patch_cim_object(
            {
                "target_type": "layer",
                "map_name": "Synthetic map",
                "layer_name": "Synthetic layer",
                "patch": {"dataConnection": {"dataset": "other"}},
                "confirm_apply": True,
            }
        ),
        "protected structural or data-connection paths",
    )
    expect_error(
        ValueError,
        lambda: server._patch_cim_object(
            {
                "target_type": "layer",
                "map_name": "Synthetic map",
                "layer_name": "Synthetic layer",
                "patch": {"visibility": False},
                "expected_cim_type": "CIMRasterLayer",
                "dry_run": True,
            }
        ),
        "CIM type changed",
    )

    applied = server._patch_cim_object(
        {
            "target_type": "layer",
            "map_name": "Synthetic map",
            "layer_name": "Synthetic layer",
            "patch": {"visibility": False},
            "expected_cim_type": "CIMFeatureLayer",
            "confirm_apply": True,
        }
    )
    assert applied["result"]["definition_replaced"] is True
    assert fake.document["visibility"] is False
    set_action, set_payload = fake.calls[-1]
    assert set_action == "set_layer_cim"
    assert set_payload["map_name"] == "Synthetic map"
    assert set_payload["layer_name"] == "Synthetic layer"
    assert set_payload["confirm_replace_definition"] is True
    assert fake.document["uRI"] == "cim://preserved"

    graphic_fake = FakeLiveBridge()
    graphic_server = make_server(graphic_fake)
    graphic_patch = graphic_server._patch_cim_object(
        {
            "target_type": "map_graphic",
            "map_name": "Synthetic map",
            "graphics_layer_name": "Graphics",
            "element_name": "Synthetic graphic",
            "patch": {"visibility": False},
            "dry_run": True,
        }
    )
    assert graphic_patch["changed_paths"] == ["$.visibility"]
    assert graphic_fake.calls[0][0] == "get_map_graphic_cim"

    print("cim patch contract ok: merge, diff, dry-run, type guard, structural guard, and confirmed apply")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
