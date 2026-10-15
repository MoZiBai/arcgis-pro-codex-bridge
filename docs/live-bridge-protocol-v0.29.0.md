# ArcGIS Codex live bridge protocol v0.29.0

Protocol v0.29.0 is a security and interoperability release for ArcGIS Pro 3.6.
It does not add new GIS actions. Project creation and layout validation are
documented in [protocol v0.30.0](live-bridge-protocol-v0.30.0.md).

## Authenticated local queue

Every job, result, and cancellation marker is wrapped in a
`hmac-sha256-v1` envelope containing an exact JSON payload string and its
HMAC-SHA256 signature. The installer generates a random 32-byte key under the
private configuration directory. Python and the Add-in reject unsigned,
tampered, or unsupported envelopes.

The installer disables inherited ACLs on the configuration directory, signing
key, and queue. Access is granted to the current user, SYSTEM, and local
Administrators. This protects against other local accounts and accidental queue
injection; it is not a security boundary against malware already running as the
same Windows user.

This envelope is intentionally incompatible with protocol v0.28.2. The MCP
runtime and Add-in must be upgraded together, followed by an ArcGIS Pro restart.

## Error handling

Full Add-in exception strings are omitted by default. Public error messages are
bounded and absolute paths are redacted. `include_debug_details=true` enables
private queue detail and local exception logging, but public MCP messages remain
redacted. The switch remains disabled in generated configs.

Tool execution failures are returned as MCP tool results with `isError=true`.
Request cancellation continues to use the JSON-RPC cancellation error.

## MCP initialization

The stdio server advertises only MCP protocol version `2024-11-05`, which is the
version covered by its contract suite. It no longer echoes arbitrary protocol
versions supplied by clients. A client that proposes another version receives
the server's supported version and can decide whether to continue.

## Validation

- `source_contract_suite.py` runs every Python source contract.
- `mcp_protocol_contract_test.py` covers version negotiation, MCP tool-error
  shape, and public error redaction.
- `queue_and_concurrency_contract_test.py` covers signed-envelope round trips,
  tamper rejection, unsigned-message rejection, cancellation, and concurrency.
- `installation_contract_test.ps1` verifies private Windows ACLs.
- GitHub Actions runs Python contracts on Windows/Linux with Python 3.10/3.12
  and PowerShell installation contracts on Windows.
