from __future__ import annotations

import json
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import ArcGISMCP


def expect_live_error(server: ArcGISMCP, action: str, payload: dict[str, object], text: str) -> str:
    try:
        server._run_live(action, payload)
    except Exception as exc:
        if text.casefold() not in str(exc).casefold():
            raise RuntimeError(f"Unexpected {action} error: {exc}") from exc
        return str(exc)
    raise RuntimeError(f"{action} unexpectedly accepted an incompatible project object")


def main() -> int:
    server = ArcGISMCP(ROOT / "config.json")
    server.live_timeout = min(server.live_timeout, 120)
    status = server._run_live("status", {})
    if status.get("protocol_version") not in {"0.22.0", "0.23.0", "0.23.1", "0.23.2", "0.24.0", "0.25.0", "0.26.0", "0.27.0", "0.27.1", "0.27.2", "0.27.3", "0.28.0"}:
        raise RuntimeError(f"Expected protocol 0.22.0 or a compatible later version, got {status.get('protocol_version')}")
    actions = set(status.get("capabilities") or [])
    required = {
        "list_task_items",
        "manage_task",
        "get_workflow_status",
        "search_workflow_jobs",
        "get_workflow_job",
        "control_workflow_job",
        "list_knowledge_graphs",
        "manage_knowledge_graph_investigation",
        "query_knowledge_graph",
        "get_link_chart_state",
        "configure_link_chart",
        "inspect_network_topology_layer",
    }
    if len(actions) < 214 or not required.issubset(actions):
        raise RuntimeError(f"Expected at least 214 actions including v0.22 additions; got {len(actions)}")

    project_path = Path(str(status["project"]))
    project_before = project_path.stat()
    map_name = str(status.get("active_map") or "")
    layers = status.get("active_layers") or []
    layer_name = str(layers[0]["name"]) if layers else None

    task_items = server._run_live("list_task_items", {})
    workflow = server._run_live("get_workflow_status", {})
    knowledge_graphs = server._run_live(
        "list_knowledge_graphs", {"include_data_model": False}
    )
    link_chart_guard = expect_live_error(
        server,
        "get_link_chart_state",
        {"map_name": map_name},
        "not a knowledge graph link chart",
    )
    if layer_name:
        network_guard = expect_live_error(
            server,
            "inspect_network_topology_layer",
            {"map_name": map_name, "layer_name": layer_name},
            "not a supported topology/network layer",
        )
    else:
        network_guard = "No layer was available for the incompatible-layer guard"

    project_after = project_path.stat()
    project_unchanged = (
        project_before.st_size == project_after.st_size
        and project_before.st_mtime_ns == project_after.st_mtime_ns
    )
    if not project_unchanged:
        raise RuntimeError("The open .aprx file changed during v0.22 validation")

    summary = {
        "handshake": {
            "addin_version": status.get("addin_version"),
            "protocol_version": status.get("protocol_version"),
            "action_count": len(actions),
        },
        "task_items": task_items,
        "workflow": workflow,
        "knowledge_graphs": knowledge_graphs,
        "type_guards": {
            "link_chart": link_chart_guard,
            "network_topology": network_guard,
        },
        "cleanup": {},
        "project_file_unchanged": project_unchanged,
        "source_data_modified": False,
        "project_saved": False,
    }
    output = ROOT / "outputs" / "bridge_runtime_tests" / "live_v022_validation_latest.json"
    output.write_text(json.dumps(summary, ensure_ascii=False, indent=2, default=str), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
