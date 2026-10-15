from __future__ import annotations

import argparse
from collections import deque
import json
import math
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time


SUPPORTED_PROTOCOL_VERSIONS = {
    "2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25",
}
PREFERRED_PROTOCOL_VERSION = "2025-11-25"


class ProbeClient:
    """Small sequential stdio client with a deadline for every request."""

    def __init__(self, command: list[str], timeout: float = 30.0):
        if not math.isfinite(timeout) or timeout <= 0:
            raise ValueError("Probe timeout must be a finite positive number")
        self.timeout = timeout
        self.process = subprocess.Popen(
            command,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        self._messages: queue.Queue[str | Exception | None] = queue.Queue()
        self._stderr: deque[str] = deque(maxlen=8)
        self._next_id = 1
        self._threads = [
            threading.Thread(target=self._read_stdout, daemon=True),
            threading.Thread(target=self._read_stderr, daemon=True),
        ]
        for thread in self._threads:
            thread.start()

    def _read_stdout(self) -> None:
        try:
            assert self.process.stdout is not None
            for line in self.process.stdout:
                self._messages.put(line)
        except Exception as error:
            self._messages.put(error)
        finally:
            self._messages.put(None)

    def _read_stderr(self) -> None:
        assert self.process.stderr is not None
        try:
            # Fixed-size reads also drain errors that have no line breaks.
            while chunk := self.process.stderr.read(1024):
                self._stderr.append(chunk)
        except (OSError, ValueError):
            pass

    def _write(self, message: dict) -> None:
        if self.process.stdin is None:
            raise RuntimeError("MCP probe input pipe is unavailable")
        self.process.stdin.write(json.dumps(message, ensure_ascii=False) + "\n")
        self.process.stdin.flush()

    def notify(self, method: str, params: dict | None = None) -> None:
        message: dict = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            message["params"] = params
        self._write(message)

    def request(self, method: str, params: dict | None = None) -> dict:
        request_id = self._next_id
        self._next_id += 1
        deadline = time.monotonic() + self.timeout
        self._write({
            "jsonrpc": "2.0", "id": request_id, "method": method,
            "params": params or {},
        })
        while True:
            remaining = deadline - time.monotonic()
            try:
                if remaining <= 0:
                    raise queue.Empty
                item = self._messages.get(timeout=remaining)
            except queue.Empty:
                try:
                    self.notify("notifications/cancelled", {
                        "requestId": request_id, "reason": "Probe request timed out",
                    })
                except (OSError, ValueError):
                    pass
                raise TimeoutError(
                    f"MCP {method} timed out after {self.timeout:g} seconds"
                ) from None
            if item is None:
                # stdout may close just before the stderr reader finishes.
                self._threads[1].join(timeout=1)
                detail = "".join(self._stderr).strip()[-2000:]
                raise RuntimeError(f"MCP server stopped during {method}: {detail}")
            if isinstance(item, Exception):
                raise RuntimeError(f"MCP output could not be read: {item}") from item
            try:
                response = json.loads(item)
            except (TypeError, ValueError) as error:
                raise RuntimeError("MCP server returned invalid JSON") from error
            if not isinstance(response, dict) or response.get("jsonrpc") != "2.0":
                raise RuntimeError("MCP server returned an invalid JSON-RPC message")
            if "method" in response:
                if "id" in response:
                    reply = {"jsonrpc": "2.0", "id": response["id"]}
                    if response["method"] == "ping":
                        reply["result"] = {}
                    else:
                        reply["error"] = {
                            "code": -32601,
                            "message": "Probe client does not support this method",
                        }
                    self._write(reply)
                # Progress/log notifications are not the response to our request.
                continue
            if response.get("id") != request_id:
                raise RuntimeError(f"MCP response ID does not match {method}")
            if "error" in response:
                raise RuntimeError(f"MCP {method} failed: {response['error']}")
            result = response.get("result")
            if not isinstance(result, dict):
                raise RuntimeError(f"MCP {method} returned an invalid result")
            if result.get("isError") is True:
                raise RuntimeError(f"MCP {method} returned isError=true")
            return result

    def close(self) -> None:
        if self.process.stdin:
            try:
                self.process.stdin.close()
            except OSError:
                pass
        try:
            self.process.wait(timeout=1)
        except subprocess.TimeoutExpired:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)
        for thread in self._threads:
            thread.join(timeout=1)
        for stream in (self.process.stdout, self.process.stderr):
            if stream:
                stream.close()


def probe(client: ProbeClient, with_arcpy: bool = False) -> dict:
    initialized = client.request("initialize", {
        "protocolVersion": PREFERRED_PROTOCOL_VERSION,
        "capabilities": {},
        "clientInfo": {"name": "arcgis-bridge-probe", "version": "2"},
    })
    version = initialized.get("protocolVersion")
    if version not in SUPPORTED_PROTOCOL_VERSIONS:
        raise RuntimeError(f"Unsupported MCP protocol negotiated: {version}")
    capabilities = initialized.get("capabilities", {})
    if not isinstance(capabilities, dict) or "tools" not in capabilities:
        raise RuntimeError("MCP server did not advertise the tools capability")
    instructions = initialized.get("instructions", "")
    if "Do not call any tool for unrelated" not in instructions:
        raise RuntimeError("MCP server instructions are missing the unrelated-task exclusion")
    client.notify("notifications/initialized")

    tools: list[dict] = []
    params: dict = {}
    cursors: set[str] = set()
    for _ in range(100):
        listed = client.request("tools/list", params)
        page = listed.get("tools")
        if not isinstance(page, list) or any(not isinstance(tool, dict) for tool in page):
            raise RuntimeError("MCP tools/list returned an invalid tools array")
        tools.extend(page)
        cursor = listed.get("nextCursor")
        if cursor is None:
            break
        if not isinstance(cursor, str) or cursor in cursors:
            raise RuntimeError("MCP tools/list returned an invalid or repeated cursor")
        cursors.add(cursor)
        params = {"cursor": cursor}
    else:
        raise RuntimeError("MCP tools/list exceeded the probe page limit")
    names = {tool.get("name") for tool in tools}
    required = {"arcgis_status", "scan_data_folder", "describe_dataset", "live_bridge_status"}
    missing = sorted(required - names)
    if missing:
        raise RuntimeError(f"Required MCP tools are missing: {missing}")

    summary: dict[str, object] = {
        "server": initialized.get("serverInfo"),
        "protocolVersion": version,
        "instructions": True,
        "toolCount": len(tools),
    }
    if with_arcpy:
        client.request("tools/call", {"name": "arcgis_status", "arguments": {}})
        summary["arcpy"] = "ok"
    return summary


def main() -> int:
    parser = argparse.ArgumentParser(description="Probe an installed ArcGIS Pro MCP server")
    parser.add_argument("--python", required=True)
    parser.add_argument("--server", type=Path, required=True)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--with-arcpy", action="store_true")
    parser.add_argument("--timeout", type=float, default=None,
                        help="Per-request deadline in seconds (default: 30; 120 with --with-arcpy)")
    args = parser.parse_args()
    client = ProbeClient(
        [args.python, str(args.server), "--config", str(args.config)],
        args.timeout if args.timeout is not None else (120.0 if args.with_arcpy else 30.0),
    )
    try:
        print(json.dumps(probe(client, args.with_arcpy), ensure_ascii=False, indent=2))
        return 0
    finally:
        client.close()


if __name__ == "__main__":
    raise SystemExit(main())
