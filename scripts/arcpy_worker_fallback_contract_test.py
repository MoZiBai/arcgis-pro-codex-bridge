from __future__ import annotations

import json
import sys
import tempfile
from pathlib import Path
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import ArcGISMCP
from src.arcgis_mcp.worker import action_status


def make_server(root: Path, *, fallback: bool = True) -> ArcGISMCP:
    data = root / "data"
    output = root / "output"
    queue = root / "queue"
    data.mkdir()
    config = {
        "arcgis_python": sys.executable,
        "allowed_data_roots": [str(data)],
        "output_root": str(output),
        "live_bridge_queue": str(queue),
        "live_arcpy_worker_fallback": fallback,
        "timeout_seconds": 30,
    }
    path = root / "config.json"
    path.write_text(json.dumps(config), encoding="utf-8")
    return ArcGISMCP(path)


def main() -> int:
    class FakeArcPy:
        @staticmethod
        def GetInstallInfo():
            return {
                "ProductName": "ArcGISPro",
                "Version": "3.6.3",
                "BuildNumber": "59527",
                "LicenseLevel": "Advanced",
                "Installer": "private-user",
                "SourceDir": "C:\\Private\\Installer",
                "InstallDate": "private-date",
            }

        @staticmethod
        def ProductInfo():
            return "ArcInfo"

    public_status = action_status(FakeArcPy(), {})
    if public_status["install"].get("Version") != "3.6.3":
        raise RuntimeError(f"Filtered ArcGIS status lost required version data: {public_status}")
    private_keys = {"Installer", "SourceDir", "InstallDate", "InstallTime"}
    if private_keys.intersection(public_status["install"]) or "executable" in public_status:
        raise RuntimeError(f"ArcGIS status exposed machine-private install metadata: {public_status}")

    with tempfile.TemporaryDirectory() as temporary:
        server = make_server(Path(temporary))
        calls: list[tuple[str, dict, int | None]] = []

        def run_live(action: str, payload: dict, timeout_seconds: int | None = None):
            calls.append((action, payload, timeout_seconds))
            return {"ok": True, "result": {"install": {"Version": "3.6.3"}}}

        server._run_live = run_live  # type: ignore[method-assign]
        blocked = RuntimeError(
            "DLL load failed: 无法成功完成操作，因为文件包含病毒或潜在的垃圾软件。"
        )
        with patch("src.arcgis_mcp.server.subprocess.Popen", side_effect=blocked):
            result = server._run_worker("status", {})
        if result["install"]["Version"] != "3.6.3":
            raise RuntimeError(f"Live ArcPy fallback returned the wrong result: {result}")
        if len(calls) != 1 or calls[0][0] != "run_arcpy_worker":
            raise RuntimeError(f"Live ArcPy fallback dispatched the wrong action: {calls}")
        if calls[0][2] != 60 or calls[0][1].get("timeout_seconds") != 30:
            raise RuntimeError(f"Live ArcPy fallback timeout was not bounded: {calls}")

    with tempfile.TemporaryDirectory() as temporary:
        server = make_server(Path(temporary))
        server._run_live = lambda *args, **kwargs: (_ for _ in ()).throw(  # type: ignore[method-assign]
            AssertionError("non-security failures must not use the live fallback")
        )
        with patch(
            "src.arcgis_mcp.server.subprocess.Popen",
            side_effect=RuntimeError("ordinary worker failure"),
        ):
            try:
                server._run_worker("status", {})
            except RuntimeError as error:
                if "ordinary worker failure" not in str(error):
                    raise
            else:
                raise RuntimeError("Ordinary worker failure was unexpectedly swallowed")

    with tempfile.TemporaryDirectory() as temporary:
        server = make_server(Path(temporary), fallback=False)
        with patch("src.arcgis_mcp.server.subprocess.Popen", side_effect=blocked):
            try:
                server._run_worker("status", {})
            except RuntimeError as error:
                if "包含病毒" not in str(error):
                    raise
            else:
                raise RuntimeError("Disabled live ArcPy fallback was unexpectedly used")

    addin = ROOT / "addin" / "ArcGISCodexBridgeAddin"
    support_source = (addin / "BridgeSupport.cs").read_text(encoding="utf-8")
    module_source = (addin / "BridgeModule.cs").read_text(encoding="utf-8")
    if (
        '"config", "config.json"' not in support_source
        or "ResolveInstallRoot()" not in support_source
        or 'TryGetProperty("live_bridge_queue"' not in support_source
        or "BridgeSupport.ResolveQueueRoot()" not in module_source
    ):
        raise RuntimeError(
            "The Add-in must resolve the live queue from the stable private config"
        )

    print(
        "arcpy worker fallback contract ok: Windows security blocks use the "
        "bounded ArcGIS Pro host, ordinary errors do not, config can disable it, "
        "the Add-in reads the stable private queue config, and status filters private install metadata"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
