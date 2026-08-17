from __future__ import annotations

import base64
import io
import json
import sys
import tempfile
import threading
import time
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from src.arcgis_mcp import server as server_module
from src.arcgis_mcp.server import (
    ArcGISMCP,
    RequestCancelled,
    _decode_signed_queue_envelope,
    _encode_signed_queue_envelope,
)


def make_server(root: Path) -> ArcGISMCP:
    data = root / "data"
    output = root / "output"
    queue = root / "queue"
    signing_key_file = root / "live-bridge.key"
    signing_key_file.write_text(
        base64.b64encode(b"queue-contract-signing-key-32b!!").decode("ascii"),
        encoding="ascii",
    )
    data.mkdir()
    config = root / "config.json"
    config.write_text(
        json.dumps(
            {
                "arcgis_python": sys.executable,
                "allowed_data_roots": [str(data)],
                "output_root": str(output),
                "live_bridge_queue": str(queue),
                "live_bridge_hmac_key_file": str(signing_key_file),
                "live_bridge_timeout_seconds": 1,
                "timeout_seconds": 10,
                "live_arcpy_worker_fallback": False,
            }
        ),
        encoding="utf-8",
    )
    return ArcGISMCP(config)


def wait_for_job(queue: Path, timeout: float = 2.0) -> Path:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        jobs = list((queue / "jobs").glob("*.json"))
        if jobs:
            return jobs[0]
        time.sleep(0.01)
    raise AssertionError("Timed out waiting for a live queue job")


def test_live_queue(server: ArcGISMCP) -> None:
    try:
        server._run_live("status", {}, timeout_seconds=0.15, _bind_identity=False)
    except TimeoutError:
        pass
    else:
        raise AssertionError("A live call with no consumer must time out")
    if list((server.live_queue / "jobs").glob("*.json")):
        raise AssertionError("Timed-out unclaimed job was not removed")
    if len(list((server.live_queue / "cancellations").glob("*.json"))) != 1:
        raise AssertionError("Timed-out live call did not write a cancellation marker")

    observed: list[dict] = []
    stop = threading.Event()

    def simulate_addin() -> None:
        while not stop.is_set():
            jobs = list((server.live_queue / "jobs").glob("*.json"))
            if not jobs:
                time.sleep(0.01)
                continue
            path = jobs[0]
            job = _decode_signed_queue_envelope(
                path.read_text(encoding="ascii"), server.live_signing_key
            )
            observed.append(job)
            path.unlink()
            result = {
                "ok": True,
                "result": {
                    "available": True,
                    "project": "C:\\Temp\\blank.aprx",
                },
                "bridge_session_id": "session-1",
                "project_path": "C:\\Temp\\blank.aprx",
            }
            result_path = server.live_queue / "results" / f"{job['id']}.json"
            result_path.write_text(
                _encode_signed_queue_envelope(result, server.live_signing_key),
                encoding="utf-8",
            )
            if job["action"] == "project_inventory":
                return

    simulator = threading.Thread(target=simulate_addin, daemon=True)
    simulator.start()
    result = server._run_live("project_inventory", {})
    stop.set()
    simulator.join(timeout=2)
    if not result.get("available"):
        raise AssertionError("Simulated bound live action did not complete")
    if [job["action"] for job in observed] != ["status", "project_inventory"]:
        raise AssertionError(f"Expected handshake followed by action, got {observed}")
    action_job = observed[1]
    required = {
        "idempotency_key",
        "created_at",
        "expires_at",
        "expected_session_id",
        "expected_project_path",
    }
    if not required.issubset(action_job):
        raise AssertionError(f"Live envelope is incomplete: {action_job}")
    if action_job["expected_session_id"] != "session-1":
        raise AssertionError("Live action was not bound to the handshake session")
    if action_job["expected_project_path"] != "C:\\Temp\\blank.aprx":
        raise AssertionError("Live action was not bound to the handshake project")


def test_queue_signatures(server: ArcGISMCP) -> None:
    payload = {"id": "signed", "action": "status", "payload": {"value": 1}}
    envelope = _encode_signed_queue_envelope(payload, server.live_signing_key)
    if _decode_signed_queue_envelope(envelope, server.live_signing_key) != payload:
        raise AssertionError("Signed queue payload did not round-trip")

    tampered = json.loads(envelope)
    tampered["payload_json"] = tampered["payload_json"].replace('"value":1', '"value":2')
    try:
        _decode_signed_queue_envelope(json.dumps(tampered), server.live_signing_key)
    except PermissionError:
        pass
    else:
        raise AssertionError("Tampered queue payload passed signature verification")

    unsigned = json.dumps(payload)
    try:
        _decode_signed_queue_envelope(unsigned, server.live_signing_key)
    except ValueError:
        pass
    else:
        raise AssertionError("Unsigned queue payload was accepted")


def test_invalid_result_is_ignored(server: ArcGISMCP) -> None:
    outcome: list[object] = []

    def invoke() -> None:
        try:
            outcome.append(
                server._run_live("status", {}, timeout_seconds=1, _bind_identity=False)
            )
        except BaseException as exc:
            outcome.append(exc)

    thread = threading.Thread(target=invoke)
    thread.start()
    job_path = wait_for_job(server.live_queue)
    job = _decode_signed_queue_envelope(
        job_path.read_text(encoding="ascii"), server.live_signing_key
    )
    result_path = server.live_queue / "results" / f"{job['id']}.json"
    result_payload = {
        "ok": True,
        "result": {"available": True},
        "bridge_session_id": "signed-session",
        "project_path": None,
    }
    result_path.write_text(json.dumps(result_payload), encoding="utf-8")
    rejection_deadline = time.monotonic() + 1
    while result_path.exists() and time.monotonic() < rejection_deadline:
        time.sleep(0.01)
    if result_path.exists():
        raise AssertionError("Unsigned bridge result was not removed")
    if not thread.is_alive():
        raise AssertionError(f"Unsigned bridge result ended the request: {outcome}")
    result_path.write_text(
        _encode_signed_queue_envelope(result_payload, server.live_signing_key),
        encoding="utf-8",
    )
    job_path.unlink(missing_ok=True)
    thread.join(timeout=3)
    if thread.is_alive() or not outcome or isinstance(outcome[0], BaseException):
        raise AssertionError(f"Valid signed bridge result did not complete: {outcome}")
    with server._live_identity_lock:
        server._live_session_id = None
        server._live_project_path = None


def test_live_cancellation(server: ArcGISMCP) -> None:
    cancellation = threading.Event()
    failure: list[BaseException] = []

    def invoke() -> None:
        try:
            server.execute_tool("live_bridge_status", {}, cancellation)
        except BaseException as exc:
            failure.append(exc)

    thread = threading.Thread(target=invoke)
    thread.start()
    job = wait_for_job(server.live_queue)
    job_id = _decode_signed_queue_envelope(
        job.read_text(encoding="ascii"), server.live_signing_key
    )["id"]
    cancellation.set()
    thread.join(timeout=3)
    if thread.is_alive() or not failure or not isinstance(failure[0], RequestCancelled):
        raise AssertionError(f"Live cancellation did not stop the request: {failure}")
    if job.exists():
        raise AssertionError("Cancelled unclaimed live job was not removed")
    if not (server.live_queue / "cancellations" / f"{job_id}.json").exists():
        raise AssertionError("Cancelled live request did not write its marker")


def test_worker_cancellation(server: ArcGISMCP) -> None:
    server._worker_command = lambda: [
        sys.executable,
        "-c",
        "import time; time.sleep(60)",
    ]
    cancellation = threading.Event()
    failure: list[BaseException] = []

    def invoke() -> None:
        server._request_local.cancellation = cancellation
        try:
            server._run_worker("status", {})
        except BaseException as exc:
            failure.append(exc)
        finally:
            server._request_local.cancellation = None

    started = time.monotonic()
    thread = threading.Thread(target=invoke)
    thread.start()
    time.sleep(0.2)
    cancellation.set()
    thread.join(timeout=15)
    if thread.is_alive() or not failure or not isinstance(failure[0], RequestCancelled):
        raise AssertionError(f"ArcPy worker cancellation failed: {failure}")
    if time.monotonic() - started >= 15:
        raise AssertionError("ArcPy worker cancellation did not terminate promptly")


class _LineStream:
    def __init__(self, lines: list[bytes]):
        self._lines = lines

    def __iter__(self):
        yield from self._lines
        time.sleep(0.15)


class _Input:
    def __init__(self, lines: list[bytes]):
        self.buffer = _LineStream(lines)


class _Output:
    def __init__(self):
        self.buffer = io.BytesIO()


class _FakeServer:
    def execute_tool(
        self,
        _name: str,
        arguments: dict,
        cancellation: threading.Event,
    ) -> dict:
        deadline = time.monotonic() + float(arguments["delay"])
        while time.monotonic() < deadline:
            if cancellation.is_set():
                raise RequestCancelled("MCP request was cancelled")
            time.sleep(0.005)
        return {"value": arguments["value"]}


def test_stdio_concurrency_and_cancel() -> None:
    messages = [
        {"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {
            "name": "fake", "arguments": {"value": "slow", "delay": 0.5}
        }},
        {"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {
            "name": "fake", "arguments": {"value": "fast", "delay": 0.02}
        }},
        {"jsonrpc": "2.0", "method": "notifications/cancelled", "params": {
            "requestId": 1, "reason": "contract test"
        }},
    ]
    encoded = [
        (json.dumps(message) + "\n").encode("utf-8") for message in messages
    ]
    fake_input = _Input(encoded)
    fake_output = _Output()
    original_input, original_output = server_module.sys.stdin, server_module.sys.stdout
    server_module.sys.stdin, server_module.sys.stdout = fake_input, fake_output
    try:
        server_module.serve(_FakeServer())
    finally:
        server_module.sys.stdin, server_module.sys.stdout = original_input, original_output

    responses = [
        json.loads(line)
        for line in fake_output.buffer.getvalue().decode("utf-8").splitlines()
    ]
    by_id = {response["id"]: response for response in responses}
    if set(by_id) != {1, 2}:
        raise AssertionError(f"Concurrent stdio responses are incomplete: {responses}")
    if by_id[1].get("error", {}).get("code") != -32800:
        raise AssertionError(f"Cancellation did not return MCP cancellation error: {by_id[1]}")
    if "fast" not in json.dumps(by_id[2]):
        raise AssertionError(f"Second concurrent request did not complete: {by_id[2]}")


def test_addin_contract_source() -> None:
    module_source = (
        ROOT / "addin" / "ArcGISCodexBridgeAddin" / "BridgeModule.cs"
    ).read_text(encoding="utf-8")
    support_source = (
        ROOT / "addin" / "ArcGISCodexBridgeAddin" / "BridgeSupport.cs"
    ).read_text(encoding="utf-8")
    source = module_source + "\n" + support_source
    required_markers = [
        "expires_at",
        "expected_session_id",
        "expected_project_path",
        "cancellations",
        "completed",
        "idempotency_key",
        "OpenVerifiedSignedPayload",
        "CreateSignedEnvelope",
        "FixedTimeEquals",
        "SafeErrorMessage",
        "WatchCancellationAsync",
    ]
    missing = [marker for marker in required_markers if marker not in source]
    if missing:
        raise AssertionError(f"Add-in queue safety contract is incomplete: {missing}")


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="arcgis-bridge-queue-contract-") as temporary:
        server = make_server(Path(temporary))
        test_queue_signatures(server)
        test_invalid_result_is_ignored(server)
        test_live_queue(server)
        test_live_cancellation(server)
        test_worker_cancellation(server)
    test_stdio_concurrency_and_cancel()
    test_addin_contract_source()
    print(
        "queue/concurrency contract ok: signed envelopes, TTL cleanup, identity binding, "
        "live cancellation, worker termination, concurrent stdio, and idempotency markers"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
