from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path


def send(process: subprocess.Popen[str], message: dict) -> dict:
    if process.stdin is None or process.stdout is None:
        raise RuntimeError("MCP probe pipes are unavailable")
    process.stdin.write(json.dumps(message, ensure_ascii=False) + "\n")
    process.stdin.flush()
    line = process.stdout.readline()
    if not line:
        stderr = process.stderr.read() if process.stderr else ""
        raise RuntimeError(f"MCP server stopped unexpectedly: {stderr}")
    return json.loads(line)


def main() -> int:
    parser = argparse.ArgumentParser(description="Probe an installed ArcGIS Pro MCP server")
    parser.add_argument("--python", required=True)
    parser.add_argument("--server", type=Path, required=True)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--with-arcpy", action="store_true")
    args = parser.parse_args()

    process = subprocess.Popen(
        [args.python, str(args.server), "--config", str(args.config)],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
    )
    try:
        initialized = send(
            process,
            {
                "jsonrpc": "2.0",
                "id": 1,
                "method": "initialize",
                "params": {
                    "protocolVersion": "2024-11-05",
                    "capabilities": {},
                    "clientInfo": {"name": "arcgis-bridge-probe", "version": "1"},
                },
            },
        )
        if "error" in initialized:
            raise RuntimeError(initialized["error"])
        result = initialized["result"]
        instructions = result.get("instructions", "")
        if "Do not call any tool for unrelated" not in instructions:
            raise RuntimeError("MCP server instructions are missing the unrelated-task exclusion")

        listed = send(
            process,
            {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}},
        )
        if "error" in listed:
            raise RuntimeError(listed["error"])
        tools = listed["result"].get("tools", [])
        names = {tool.get("name") for tool in tools}
        required = {"arcgis_status", "scan_data_folder", "describe_dataset", "live_bridge_status"}
        missing = sorted(required - names)
        if missing:
            raise RuntimeError(f"Required MCP tools are missing: {missing}")

        summary: dict[str, object] = {
            "server": result.get("serverInfo"),
            "protocolVersion": result.get("protocolVersion"),
            "instructions": True,
            "toolCount": len(tools),
        }
        if args.with_arcpy:
            status = send(
                process,
                {
                    "jsonrpc": "2.0",
                    "id": 3,
                    "method": "tools/call",
                    "params": {"name": "arcgis_status", "arguments": {}},
                },
            )
            if "error" in status:
                raise RuntimeError(status["error"])
            summary["arcpy"] = "ok"

        print(json.dumps(summary, ensure_ascii=False, indent=2))
        return 0
    finally:
        if process.stdin:
            process.stdin.close()
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


if __name__ == "__main__":
    raise SystemExit(main())
