from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SERVER = ROOT / "src" / "arcgis_mcp" / "server.py"
CONFIG = ROOT / "config.json"


def send(process: subprocess.Popen[str], message: dict) -> dict:
    assert process.stdin is not None
    assert process.stdout is not None
    process.stdin.write(json.dumps(message, ensure_ascii=False) + "\n")
    process.stdin.flush()
    line = process.stdout.readline()
    if not line:
        stderr = process.stderr.read() if process.stderr else ""
        raise RuntimeError(f"MCP server stopped unexpectedly: {stderr}")
    return json.loads(line)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--with-arcpy", action="store_true")
    parser.add_argument("--scan-folder")
    parser.add_argument("--describe")
    parser.add_argument("--field-values", nargs=2, metavar=("PATH", "FIELD"))
    args = parser.parse_args()

    process = subprocess.Popen(
        [sys.executable, str(SERVER), "--config", str(CONFIG)],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
    )
    try:
        initialized = send(
            process,
            {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "smoke-test", "version": "1"}}},
        )
        tools = send(process, {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
        tool_names = [tool["name"] for tool in tools["result"]["tools"]]
        print(json.dumps({"initialize": initialized["result"], "tools": tool_names}, ensure_ascii=False, indent=2))

        if args.with_arcpy:
            status = send(process, {"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "arcgis_status", "arguments": {}}})
            print(json.dumps(status, ensure_ascii=False, indent=2))
            if "error" in status:
                return 2
        if args.scan_folder:
            scan = send(
                process,
                {
                    "jsonrpc": "2.0",
                    "id": 4,
                    "method": "tools/call",
                    "params": {
                        "name": "scan_data_folder",
                        "arguments": {"folder": args.scan_folder, "recursive": True, "max_items": 20},
                    },
                },
            )
            print(json.dumps(scan, ensure_ascii=False, indent=2))
            if "error" in scan:
                return 3
        if args.describe:
            description = send(
                process,
                {
                    "jsonrpc": "2.0",
                    "id": 5,
                    "method": "tools/call",
                    "params": {"name": "describe_dataset", "arguments": {"path": args.describe}},
                },
            )
            print(json.dumps(description, ensure_ascii=False, indent=2))
            if "error" in description:
                return 4
        if args.field_values:
            path, field = args.field_values
            values = send(
                process,
                {
                    "jsonrpc": "2.0",
                    "id": 6,
                    "method": "tools/call",
                    "params": {
                        "name": "list_field_values",
                        "arguments": {"path": path, "field": field, "max_values": 1000},
                    },
                },
            )
            print(json.dumps(values, ensure_ascii=False, indent=2))
            if "error" in values:
                return 5
        return 0
    finally:
        if process.stdin:
            process.stdin.close()
        process.terminate()
        process.wait(timeout=5)


if __name__ == "__main__":
    raise SystemExit(main())
