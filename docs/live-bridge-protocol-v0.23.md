# ArcGIS Codex live bridge protocol v0.23

Protocol v0.23 adds five direct actions, raising the ArcGIS Pro SDK action
count from 214 to 219. The new Framework layer provides a configuration-driven
escape hatch for long-tail ArcGIS Pro commands and panes that do not yet merit
a dedicated typed bridge action.

## Read-only Framework inspection

- `get_framework_state` reports application initialization/busy state, active
  ribbon tab and tool, ribbon/backstage state, active panes, dock panes, theme,
  display scale, and optionally installed add-in metadata.
- `inspect_framework_command` resolves an exact ArcGIS Pro command ID through
  `FrameworkApplication.GetPlugInWrapper` and reports its command type, caption,
  enabled/relevant/checked state, tooltip, keytip and child command IDs. It does
  not execute the command.

These two actions are available while generic Framework UI control is disabled.

The MCP-only `search_arcgis_framework_registry` helper indexes the installed
ArcGIS Pro DAML and `CommandSearchTerms.xml` registries. It supports exact IDs,
English and localized captions, search terms, extension/kind filters and
conservative side-effect risk hints. This read-only discovery helper does not
change the 219 direct add-in action count and does not require an ArcGIS Pro
restart when its Python implementation is updated.

## Gated Framework control

- `execute_framework_command` executes an exact command ID, optionally with a
  bounded argument array.
- `configure_framework_application` can activate an exact ribbon tab or tool,
  minimize the ribbon, close Backstage, or open an exact Backstage page.
- `manage_framework_pane` can create, activate, hide, pin, unpin, close, or
  flash an exact pane or dock-pane ID.

All three control actions require `allow_framework_ui_control=true`. IDs must
also appear in their separate exact, case-insensitive configuration allowlists:

- `allowed_framework_commands`
- `allowed_framework_panes`
- `allowed_framework_tabs`
- `allowed_framework_tools`
- `allowed_backstage_pages`

The add-in independently requires `confirm_execute=true`,
`confirm_ui_change=true`, or `confirm_pane_action=true` as appropriate. Batch
execution passes every nested action through the same MCP preparation and
safety gates, so `live_execute_batch` cannot bypass the allowlists.

## Safety model

Framework command execution is deliberately classified as potentially changing
project or external state because ArcGIS Pro command IDs span save, edit,
publishing and extension-specific operations. The default configuration leaves
the master switch false and every allowlist empty. Read-only inspection is the
recommended discovery path; a command or pane should be allowlisted only after
its exact ID and side effects have been reviewed.

Protocol v0.23 does not change the existing source-editing, destructive-tool,
schema, publishing, or Workflow Manager gates. It also does not save the open
project automatically.
