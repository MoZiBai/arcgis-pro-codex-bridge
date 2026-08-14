from __future__ import annotations

import json
import subprocess
import sys
import time
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SERVER = ROOT / "src" / "arcgis_mcp" / "server.py"
CONFIG = ROOT / "config.json"
OUTPUTS = ROOT / "outputs"


def send(process: subprocess.Popen[str], request_id: int, name: str, arguments: dict) -> dict:
    assert process.stdin is not None
    assert process.stdout is not None
    message = {
        "jsonrpc": "2.0",
        "id": request_id,
        "method": "tools/call",
        "params": {"name": name, "arguments": arguments},
    }
    process.stdin.write(json.dumps(message, ensure_ascii=True) + "\n")
    process.stdin.flush()
    response = json.loads(process.stdout.readline())
    if "error" in response:
        raise RuntimeError(response["error"]["message"])
    return response


def main() -> int:
    OUTPUTS.mkdir(parents=True, exist_ok=True)
    suffix = time.strftime("%Y%m%d_%H%M%S")
    gdb_name = f"mcp_synthetic_{suffix}.gdb"
    gdb_path = OUTPUTS / gdb_name
    feature_class = gdb_path / "test_points"

    process = subprocess.Popen(
        [sys.executable, str(SERVER), "--config", str(CONFIG)],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
    )
    try:
        create_gdb = send(
            process,
            1,
            "run_geoprocessing_tool",
            {"tool": "management.CreateFileGDB", "args": [str(OUTPUTS), gdb_name]},
        )
        create_fc = send(
            process,
            2,
            "run_geoprocessing_tool",
            {
                "tool": "management.CreateFeatureclass",
                "args": [str(gdb_path), "test_points", "POINT", "", "DISABLED", "DISABLED", 4326],
            },
        )
        description = send(process, 3, "describe_dataset", {"path": str(feature_class)})
        print(
            json.dumps(
                {
                    "synthetic_only": True,
                    "geodatabase": str(gdb_path),
                    "feature_class": str(feature_class),
                    "create_gdb": create_gdb["result"],
                    "create_feature_class": create_fc["result"],
                    "description": description["result"],
                },
                ensure_ascii=False,
                indent=2,
            )
        )
        return 0
    finally:
        if process.stdin:
            process.stdin.close()
        process.terminate()
        process.wait(timeout=5)


if __name__ == "__main__":
    raise SystemExit(main())
