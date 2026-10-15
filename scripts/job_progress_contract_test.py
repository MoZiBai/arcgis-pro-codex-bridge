from __future__ import annotations

import io
import json
import sys
import threading
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp.server import JobRegistry, serve


class _LineStream:
    def __init__(self, messages: list[dict]):
        self._lines = [(json.dumps(message) + "\n").encode("utf-8") for message in messages]

    def __iter__(self):
        yield from self._lines


class _Input:
    def __init__(self, messages: list[dict]):
        self.buffer = _LineStream(messages)


class _Output:
    def __init__(self):
        self.buffer = io.BytesIO()


class _Server:
    def __init__(self):
        self.jobs = JobRegistry(ROOT)

    def execute_tool(
        self,
        _name: str,
        _arguments: dict,
        cancellation: threading.Event,
        job_id: str,
    ) -> dict:
        self.jobs.running(job_id)
        self.jobs.update(job_id, "processing", 0.5)
        if cancellation.is_set():
            raise RuntimeError("cancelled")
        result = {"output": str(ROOT / "contract-output.pdf")}
        self.jobs.finish(job_id, status="completed", result=result)
        return result


def main() -> int:
    events: list[dict] = []
    registry = JobRegistry(ROOT)
    registry.set_emitter(events.append)
    cancellation = threading.Event()
    registry.start("17", 17, "test_tool", cancellation, "token-17")
    registry.running("17")
    registry.update("17", "processing", 0.5)
    registry.finish("17", status="completed", result={"output": str(ROOT / "artifact.pdf")})
    status = registry.status("17")
    if status["status"] != "completed" or not status["outputs"]:
        raise AssertionError(f"Completed job did not retain output inventory: {status}")
    if not any(event.get("method") == "notifications/progress" for event in events):
        raise AssertionError("Progress notifications were not emitted")

    registry.start("18", 18, "long_tool", cancellation)
    cancelled = registry.cancel("18")
    if not cancelled["cancel_requested"] or not cancellation.is_set():
        raise AssertionError("Active job cancellation did not set its event")

    fake_server = _Server()
    fake_input = _Input(
        [
            {
                "jsonrpc": "2.0",
                "id": 1,
                "method": "tools/call",
                "params": {
                    "name": "test_tool",
                    "arguments": {},
                    "_meta": {"progressToken": "stdio-token"},
                },
            }
        ]
    )
    fake_output = _Output()
    original_input, original_output = sys.stdin, sys.stdout
    sys.stdin, sys.stdout = fake_input, fake_output
    try:
        serve(fake_server)
    finally:
        sys.stdin, sys.stdout = original_input, original_output
    messages = [
        json.loads(line)
        for line in fake_output.buffer.getvalue().decode("utf-8").splitlines()
    ]
    if not any(message.get("method") == "notifications/progress" for message in messages):
        raise AssertionError("stdio progress notification was not emitted")
    response = next(message for message in messages if message.get("id") == 1)
    if response.get("result", {}).get("structuredContent", {}).get("job_id") != "1":
        raise AssertionError(f"Job id was not returned in structuredContent: {response}")

    print("job progress contract ok: bounded history, output inventory, cancellation, and MCP progress notifications")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
