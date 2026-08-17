from __future__ import annotations

import base64
import json
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import ArcGISMCP


def expect_permission_error(callable_, expected: str) -> None:
    try:
        callable_()
    except PermissionError as exc:
        if expected not in str(exc):
            raise AssertionError(f"Unexpected permission error: {exc}") from exc
    else:
        raise AssertionError("Expected PermissionError")


def make_server() -> tuple[ArcGISMCP, tempfile.TemporaryDirectory[str]]:
    temporary = tempfile.TemporaryDirectory(prefix="arcgis-bridge-safety-")
    root = Path(temporary.name)
    signing_key = root / "live-bridge.key"
    signing_key.write_text(
        base64.b64encode(b"safety-contract-signing-key-32bytes").decode("ascii"),
        encoding="ascii",
    )
    config = root / "config.json"
    config.write_text(
        json.dumps(
            {
                "arcgis_python": sys.executable,
                "allowed_data_roots": [str(ROOT)],
                "output_root": str(root / "output"),
                "live_bridge_queue": str(root / "queue"),
                "live_bridge_hmac_key_file": str(signing_key),
            }
        ),
        encoding="utf-8",
    )
    return ArcGISMCP(config), temporary


def main() -> int:
    server, temporary = make_server()

    assert server.allow_source_editing is False
    assert server.allow_destructive is False
    assert server.allow_schema_changes is False
    assert server.allow_external_publishing is False
    assert server.allow_external_workflow_actions is False
    assert server.allow_framework_ui_control is False
    assert server.allowed_framework_commands == set()
    assert server.allowed_framework_panes == set()
    assert server.allowed_framework_tabs == set()
    assert server.allowed_framework_tools == set()
    assert server.allowed_backstage_pages == set()

    expect_permission_error(
        lambda: server._prepare_live_action(
            "update_feature_attributes",
            {
                "layer_name": "synthetic",
                "updates": [{"object_id": 1, "attributes": {"name": "test"}}],
                "confirm_source_edit": True,
            },
        ),
        "allow_source_editing=false",
    )
    expect_permission_error(
        lambda: server._prepare_live_action(
            "add_attachment",
            {
                "map_member_name": "synthetic",
                "object_id": 1,
                "file_path": str(ROOT / "README.md"),
                "confirm_source_edit": True,
            },
        ),
        "allow_source_editing=false",
    )
    expect_permission_error(
        lambda: server._prepare_live_action(
            "control_operation_history",
            {"operation": "undo", "confirm_source_edit": True},
        ),
        "allow_source_editing=false",
    )
    expect_permission_error(
        lambda: server._prepare_live_action(
            "evaluate_attribute_rules",
            {"member_name": "synthetic", "confirm_evaluate": True},
        ),
        "allow_source_editing=false",
    )

    server.allow_source_editing = True
    server._prepare_live_action(
        "update_feature_attributes",
        {
            "layer_name": "synthetic",
            "updates": [{"object_id": 1, "attributes": {"name": "test"}}],
            "confirm_source_edit": True,
        },
    )
    prepared_attachment = server._prepare_live_action(
        "add_attachment",
        {
            "map_member_name": "synthetic",
            "object_id": 1,
            "file_path": str(ROOT / "README.md"),
            "confirm_source_edit": True,
        },
    )
    assert prepared_attachment["file_path"] == str((ROOT / "README.md").resolve())
    expect_permission_error(
        lambda: server._prepare_live_action(
            "delete_features",
            {
                "layer_name": "synthetic",
                "object_ids": [1],
                "confirm_source_edit": True,
                "confirm_delete": True,
            },
        ),
        "allow_destructive_tools=false",
    )
    expect_permission_error(
        lambda: server._prepare_live_action(
            "remove_attachments",
            {
                "map_member_name": "synthetic",
                "object_id": 1,
                "attachment_ids": [1],
                "confirm_source_edit": True,
                "confirm_delete": True,
            },
        ),
        "allow_destructive_tools=false",
    )

    expect_permission_error(
        lambda: server._validate_schema_tool("management.AddField"),
        "allow_schema_changes=false",
    )
    expect_permission_error(
        lambda: server._validate_publishing_tool("server.UploadServiceDefinition"),
        "allow_external_publishing=false",
    )
    expect_permission_error(
        server._require_workflow_actions,
        "allow_external_workflow_actions=false",
    )
    expect_permission_error(
        lambda: server._require_framework_ui("command", "esri_test_command"),
        "allow_framework_ui_control=false",
    )
    expect_permission_error(
        lambda: server._prepare_live_action(
            "execute_framework_command",
            {"command_id": "esri_test_command", "confirm_execute": True},
        ),
        "allow_framework_ui_control=false",
    )
    expect_permission_error(
        lambda: server.call_tool(
            "live_execute_batch",
            {
                "commands": [
                    {
                        "action": "execute_framework_command",
                        "payload": {
                            "command_id": "esri_test_command",
                            "confirm_execute": True,
                        },
                    }
                ]
            },
        ),
        "allow_framework_ui_control=false",
    )
    expect_permission_error(
        lambda: server.call_tool(
            "live_execute_batch",
            {
                "commands": [
                    {
                        "action": "control_workflow_job",
                        "payload": {
                            "operation": "stop_steps",
                            "job_id": "test-job",
                            "step_ids": ["test-step"],
                            "confirm_workflow_action": True,
                        },
                    }
                ]
            },
        ),
        "allow_external_workflow_actions=false",
    )

    server.allow_framework_ui_control = True
    server.allowed_framework_commands = {"esri_allowed_command"}
    server._require_framework_ui("command", "ESRI_ALLOWED_COMMAND")
    expect_permission_error(
        lambda: server._require_framework_ui("command", "esri_not_allowed"),
        "not in the exact config allowlist",
    )

    print("safety contract ok: source edits, destructive edits, schema changes, publishing, workflow mutations, and generic framework UI control default to disabled")
    temporary.cleanup()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
