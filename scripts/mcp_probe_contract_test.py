from __future__ import annotations

import math
import sys
import unittest

from mcp_probe import ProbeClient, probe


FAKE_SERVER = r"""
import json, sys, time
scenario = sys.argv[1]
if scenario == "startup_error":
    sys.stderr.write("startup fixture failure")
    sys.exit(2)
ready = False
def emit(value):
    print(json.dumps(value), flush=True)
for line in sys.stdin:
    request = json.loads(line)
    method = request.get("method")
    if method == "notifications/initialized":
        ready = True
        continue
    if "id" not in request:
        continue
    request_id = request["id"]
    if method == "initialize":
        if scenario == "timeout":
            time.sleep(10)
        if scenario == "stderr":
            sys.stderr.write("x" * 100000)
            sys.stderr.flush()
        result = {
            "protocolVersion": "2099-01-01" if scenario == "version" else "2024-11-05",
            "capabilities": {"tools": {}},
            "instructions": "Do not call any tool for unrelated requests",
            "serverInfo": {"name": "fixture", "version": "1"},
        }
    elif method == "tools/list":
        if not ready:
            emit({"jsonrpc": "2.0", "id": request_id, "error": {"code": -32000, "message": "not initialized"}})
            continue
        names = ["arcgis_status", "scan_data_folder", "describe_dataset", "live_bridge_status"]
        if scenario == "missing":
            names.pop()
        if scenario in ("pages", "cursor"):
            if request.get("params", {}).get("cursor"):
                names = names[2:]
            else:
                names = names[:2]
        result = {"tools": [{"name": name} for name in names]}
        if scenario == "cursor" or (scenario == "pages" and not request.get("params", {}).get("cursor")):
            result["nextCursor"] = "page2"
    else:
        result = {"content": [{"type": "text", "text": "status"}], "isError": scenario == "tool_error"}
    if scenario == "notifications":
        emit({"jsonrpc": "2.0", "method": "notifications/progress", "params": {"progress": 1}})
    if scenario == "wrong_id":
        request_id += 10
    emit({"jsonrpc": "2.0", "id": request_id, "result": result})
"""


class ProbeContract(unittest.TestCase):
    def check_scenario(self, scenario: str, timeout: float = 5.0) -> dict:
        client = ProbeClient([sys.executable, "-u", "-c", FAKE_SERVER, scenario], timeout)
        try:
            return probe(client, with_arcpy=True)
        finally:
            client.close()
            self.assertIsNotNone(client.process.poll())

    def test_initialization_notification_and_legacy_negotiation(self):
        summary = self.check_scenario("normal")
        self.assertEqual(summary["protocolVersion"], "2024-11-05")
        self.assertEqual(summary["arcpy"], "ok")
        self.assertEqual(summary["toolCount"], 4)

    def test_progress_is_not_treated_as_response(self):
        self.assertEqual(self.check_scenario("notifications")["arcpy"], "ok")

    def test_stderr_is_drained(self):
        self.assertEqual(self.check_scenario("stderr")["arcpy"], "ok")

    def test_tool_error_is_not_reported_as_healthy(self):
        with self.assertRaisesRegex(RuntimeError, "isError=true"):
            self.check_scenario("tool_error")

    def test_unknown_protocol_is_rejected(self):
        with self.assertRaisesRegex(RuntimeError, "Unsupported MCP protocol"):
            self.check_scenario("version")

    def test_response_id_is_checked(self):
        with self.assertRaisesRegex(RuntimeError, "response ID"):
            self.check_scenario("wrong_id")

    def test_missing_required_tools_fail(self):
        with self.assertRaisesRegex(RuntimeError, "Required MCP tools"):
            self.check_scenario("missing")

    def test_tools_list_pagination(self):
        self.assertEqual(self.check_scenario("pages")["toolCount"], 4)

    def test_repeated_cursor_fails(self):
        with self.assertRaisesRegex(RuntimeError, "repeated cursor"):
            self.check_scenario("cursor")

    def test_request_timeout(self):
        with self.assertRaisesRegex(TimeoutError, "timed out"):
            self.check_scenario("timeout", timeout=0.2)

    def test_startup_error_preserves_stderr(self):
        with self.assertRaisesRegex(RuntimeError, "startup fixture failure"):
            self.check_scenario("startup_error")

    def test_invalid_timeout(self):
        for value in (0, -1, math.inf, math.nan):
            with self.subTest(value=value), self.assertRaises(ValueError):
                ProbeClient([], timeout=value)


if __name__ == "__main__":
    unittest.main()
