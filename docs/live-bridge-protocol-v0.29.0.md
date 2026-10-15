# ArcGIS Codex live bridge protocol v0.29.0

Protocol v0.29.0 defines the authenticated queue for ArcGIS Pro 3.6.
The current source adds optional actions while keeping the queue and safety
contracts. These additions are not included in the published v0.29.0 ZIP.

## Source additions, not included in the published v0.29.0 ZIP

### Project creation without a current project

`create_blank_project` is implemented with the public
`Project.CreateAsync(CreateProjectSettings)` SDK API and uses the `Untitled`
template type. It can run when `Project.Current` is null and creates a new
editable `.aprx` under the configured output root. Optional fields bootstrap a
map, layout, map frame, and data references after creation; source datasets are
read-only by default. The MCP route is `live_create_blank_project`.

The caller must provide `output_aprx`; the bridge does not silently choose a
project location. The value is normalized under the configured output root and
means the exact final project file, not a containing directory. Creation fails
unless ArcGIS Pro's current project path equals that value and the file exists.
SDK-created default geodatabase, toolbox, and indexing artifacts live in the
reported sibling `project_support_folder`.

Creation first materializes the SDK's untitled project inside the reported
support folder, then persists the requested base structure, and finally invokes
`SaveAsAsync` for the caller's exact output file. This two-stage ordering avoids
ArcGIS Pro's untitled-project folder interpretation and is covered by the
lifecycle test.

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
