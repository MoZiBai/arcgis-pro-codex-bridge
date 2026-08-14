from __future__ import annotations

import json
import shutil
import sys
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import ArcGISMCP, TOOLS


def main() -> int:
    names = {tool["name"] for tool in TOOLS}
    if "reload_arcgis_mcp_policy" not in names:
        raise RuntimeError("Policy reload tool is not registered")

    output_root = ROOT / "outputs" / "bridge_runtime_tests"
    output_root.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="policy_reload_", dir=output_root) as directory:
        config_path = Path(directory) / "config.json"
        shutil.copy2(ROOT / "config.json", config_path)
        server = ArcGISMCP(config_path)
        if server.allow_framework_ui_control:
            raise RuntimeError("Test requires the default Framework gate to be false")

        config = json.loads(config_path.read_text(encoding="utf-8"))
        config["allow_framework_ui_control"] = True
        config["allowed_framework_commands"] = ["esri_core_saveProjectButton"]
        config_path.write_text(json.dumps(config, ensure_ascii=False, indent=2), encoding="utf-8")

        result = server.call_tool("reload_arcgis_mcp_policy", {"confirm_reload": True})
        if result["after"]["allow_framework_ui_control"] is not True:
            raise RuntimeError(f"Framework master gate did not reload: {result}")
        if result["after"]["allowed_framework_commands"] != ["esri_core_saveprojectbutton"]:
            raise RuntimeError(f"Exact command allowlist did not reload: {result}")
        if result["paths_and_runtime_reloaded"] is not False:
            raise RuntimeError("Policy reload unexpectedly changed static path/runtime settings")
        if result["arcgis_pro_restart_required"] is not False:
            raise RuntimeError("Policy reload incorrectly requires an ArcGIS Pro restart")
        server._require_framework_ui("command", "ESRI_CORE_SAVEPROJECTBUTTON")

    print("policy reload contract ok: safety gates and exact Framework allowlists hot-reload without ArcGIS Pro or MCP restart")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
