from __future__ import annotations

import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import ArcGISMCP, TOOLS


def main() -> int:
    names = {tool["name"] for tool in TOOLS}
    required = {
        "live_get_portal_state",
        "live_inspect_time_zones",
        "live_inspect_locators",
        "live_geocode_location",
        "live_get_operation_history",
        "live_control_operation_history",
    }
    if not required.issubset(names):
        raise RuntimeError(f"Application integration tools are missing: {required - names}")

    source = (
        ROOT / "addin" / "ArcGISCodexBridgeAddin" / "ApplicationIntegrationActions.cs"
    ).read_text(encoding="utf-8")
    required_source = {
        "credentials_returned = false",
        "tokens_returned = false",
        "GeocodeAsync(location, false, false, false)",
        "GetLocatorInformationAsync()",
        "ProApp.IANATimeZoneManager",
        "FindUndoOperations(_ => true)",
        "confirm_operation=true is required",
        "project_saved = false",
    }
    missing = sorted(value for value in required_source if value not in source)
    if missing:
        raise RuntimeError(f"Application integration safety contract is incomplete: {missing}")
    if "GetToken()" in source or "SignInWithCredentials" in source:
        raise RuntimeError("Portal inspection must not expose tokens or credential operations")

    server = ArcGISMCP(ROOT / "config.json")
    try:
        server.call_tool(
            "live_control_operation_history",
            {"scope": "map", "operation": "undo", "confirm_operation": True},
        )
    except PermissionError:
        pass
    else:
        raise RuntimeError("Operation-history mutation bypassed the source-editing safety gate")

    print("application integration contract ok: portal/time-zone/locator/history reads are bounded and operation mutation remains gated")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
