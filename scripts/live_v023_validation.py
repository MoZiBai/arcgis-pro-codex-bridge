from __future__ import annotations

import json
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import ArcGISMCP


def expect_permission_error(callable_, expected: str) -> str:
    try:
        callable_()
    except PermissionError as exc:
        if expected.casefold() not in str(exc).casefold():
            raise RuntimeError(f"Unexpected permission error: {exc}") from exc
        return str(exc)
    raise RuntimeError("A gated Framework action unexpectedly passed MCP safety checks")


def main() -> int:
    server = ArcGISMCP(ROOT / "config.json")
    server.live_timeout = min(server.live_timeout, 120)
    status = server._run_live("status", {})
    if status.get("protocol_version") not in {"0.23.2", "0.24.0", "0.25.0", "0.26.0", "0.27.0", "0.27.1", "0.27.2", "0.27.3"}:
        raise RuntimeError(f"Expected protocol 0.23.2 or later compatible version, got {status.get('protocol_version')}")
    actions = set(status.get("capabilities") or [])
    required = {
        "get_framework_state",
        "inspect_framework_command",
        "execute_framework_command",
        "configure_framework_application",
        "manage_framework_pane",
    }
    if len(actions) < 219 or not required.issubset(actions):
        raise RuntimeError(f"Expected at least 219 actions including v0.23 additions; got {len(actions)}")

    project_path = Path(str(status["project"]))
    project_before = project_path.stat()
    framework_state = server._run_live("get_framework_state", {"include_addins": True})
    command_id = "esri_core_saveProjectButton"
    command = server._run_live(
        "inspect_framework_command",
        {"command_id": command_id, "load_on_demand": True},
    )
    if command.get("found") is not True:
        raise RuntimeError(f"Known ArcGIS Pro Core command was not found: {command}")

    command_guard = expect_permission_error(
        lambda: server.call_tool(
            "live_execute_framework_command",
            {"command_id": command_id, "confirm_execute": True},
        ),
        "allow_framework_ui_control=false",
    )
    pane_guard = expect_permission_error(
        lambda: server.call_tool(
            "live_manage_framework_pane",
            {
                "operation": "flash",
                "pane_id": "esri_core_projectDockPane",
                "confirm_pane_action": True,
            },
        ),
        "allow_framework_ui_control=false",
    )

    project_after = project_path.stat()
    project_unchanged = (
        project_before.st_size == project_after.st_size
        and project_before.st_mtime_ns == project_after.st_mtime_ns
    )
    if not project_unchanged:
        raise RuntimeError("The open .aprx file changed during v0.23 validation")

    summary = {
        "handshake": {
            "addin_version": status.get("addin_version"),
            "protocol_version": status.get("protocol_version"),
            "action_count": len(actions),
        },
        "framework_state": framework_state,
        "inspected_command": command,
        "safety_guards": {
            "command_execution": command_guard,
            "pane_control": pane_guard,
        },
        "cleanup": {},
        "project_file_unchanged": project_unchanged,
        "source_data_modified": False,
        "project_saved": False,
    }
    output = ROOT / "outputs" / "bridge_runtime_tests" / "live_v023_validation_latest.json"
    output.write_text(json.dumps(summary, ensure_ascii=False, indent=2, default=str), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
