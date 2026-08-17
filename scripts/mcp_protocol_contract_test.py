from __future__ import annotations

import io
import json
import sys
import threading
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp import server as server_module
from src.arcgis_mcp.server import (
    MCP_PROTOCOL_VERSION,
    SUPPORTED_MCP_PROTOCOL_VERSIONS,
    _negotiate_mcp_protocol_version,
    _safe_error_message,
)


class _LineStream:
    def __init__(self, messages: list[dict]):
        self._lines = [
            (json.dumps(message) + "\n").encode("utf-8") for message in messages
        ]

    def __iter__(self):
        yield from self._lines


class _Input:
    def __init__(self, messages: list[dict]):
        self.buffer = _LineStream(messages)


class _Output:
    def __init__(self):
        self.buffer = io.BytesIO()


class _FailingServer:
    def execute_tool(
        self,
        _name: str,
        _arguments: dict,
        _cancellation: threading.Event,
    ) -> dict:
        raise RuntimeError(r"C:\Users\PrivateUser\secret\dataset.gdb failed")


def capture(messages: list[dict]) -> list[dict]:
    fake_input = _Input(messages)
    fake_output = _Output()
    original_input, original_output = server_module.sys.stdin, server_module.sys.stdout
    server_module.sys.stdin, server_module.sys.stdout = fake_input, fake_output
    try:
        server_module.serve(_FailingServer())
    finally:
        server_module.sys.stdin, server_module.sys.stdout = original_input, original_output
    return [
        json.loads(line)
        for line in fake_output.buffer.getvalue().decode("utf-8").splitlines()
    ]


def main() -> int:
    if MCP_PROTOCOL_VERSION not in SUPPORTED_MCP_PROTOCOL_VERSIONS:
        raise AssertionError("Default MCP protocol version is not advertised as supported")
    if _negotiate_mcp_protocol_version(MCP_PROTOCOL_VERSION) != MCP_PROTOCOL_VERSION:
        raise AssertionError("Supported MCP protocol version was not preserved")
    if _negotiate_mcp_protocol_version("2099-01-01") != MCP_PROTOCOL_VERSION:
        raise AssertionError("Unsupported MCP protocol version was echoed to the client")

    redacted = _safe_error_message(
        RuntimeError(r"Failed at C:\Users\PrivateUser\secret\dataset.gdb")
    )
    if "PrivateUser" in redacted or "dataset.gdb" in redacted or "<path>" not in redacted:
        raise AssertionError(f"Absolute path was not redacted: {redacted}")

    responses = capture(
        [
            {
                "jsonrpc": "2.0",
                "id": 1,
                "method": "initialize",
                "params": {
                    "protocolVersion": "2099-01-01",
                    "capabilities": {},
                    "clientInfo": {"name": "contract", "version": "1"},
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 2,
                "method": "tools/call",
                "params": {"name": "failing", "arguments": {}},
            },
        ]
    )
    by_id = {response["id"]: response for response in responses}
    if by_id[1]["result"]["protocolVersion"] != MCP_PROTOCOL_VERSION:
        raise AssertionError(f"Initialize negotiation failed: {by_id[1]}")
    tool_result = by_id[2].get("result")
    if not tool_result or tool_result.get("isError") is not True or "error" in by_id[2]:
        raise AssertionError(f"Tool execution error did not use MCP isError result: {by_id[2]}")
    serialized = json.dumps(tool_result)
    if "PrivateUser" in serialized or "dataset.gdb" in serialized:
        raise AssertionError(f"Tool error leaked an absolute path: {tool_result}")

    print(
        "MCP protocol contract ok: fixed version negotiation, tool isError results, "
        "and path-redacted public errors"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
