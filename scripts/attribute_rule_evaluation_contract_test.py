from __future__ import annotations

import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import ArcGISMCP, TOOLS


def main() -> int:
    if not any(item["name"] == "live_evaluate_attribute_rules" for item in TOOLS):
        raise RuntimeError("Attribute-rule evaluation MCP tool is not registered")
    source = (
        ROOT / "addin" / "ArcGISCodexBridgeAddin" / "AttributeRuleActions.cs"
    ).read_text(encoding="utf-8")
    required = {
        "GetAttributeRuleManager", "IsEvaluationSupported", "manager.Evaluate(description)",
        "confirm_evaluate=true is required", "source_data_modified = true",
        "project_saved = false", "AttributeRuleEvaluationDescription",
    }
    missing = sorted(value for value in required if value not in source)
    if missing:
        raise RuntimeError(f"Attribute-rule evaluation contract is incomplete: {missing}")
    server = ArcGISMCP(ROOT / "config.json")
    try:
        server.call_tool(
            "live_evaluate_attribute_rules",
            {"member_name": "not-used", "confirm_evaluate": True},
        )
    except PermissionError:
        pass
    else:
        raise RuntimeError("Attribute-rule evaluation bypassed the source-editing safety gate")
    print("attribute rule evaluation contract ok: confirmed evaluation remains disabled behind the source-editing gate")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
