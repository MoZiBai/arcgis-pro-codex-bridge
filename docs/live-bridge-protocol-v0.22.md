# ArcGIS Codex live bridge protocol v0.22

Protocol v0.22 adds 12 direct actions, raising the ArcGIS Pro SDK action count
from 202 to 214. It covers Task Assistant, Workflow Manager, Knowledge Graphs,
link charts and typed topology/network inspection.

## Task Assistant

- `list_task_items` inventories project task items, task GUIDs, names and group
  paths.
- `manage_task` opens or closes task items, opens a specific task, imports or
  opens `.esriTasks` files from allowed roots, and exports task items under the
  configured output root.

These actions change only project/UI task state and exported task files. They do
not execute a task's internal steps automatically.

## Workflow Manager

- `get_workflow_status` reports availability, connection, service item and
  current job context.
- `search_workflow_jobs` and `get_workflow_job` provide bounded read-only job,
  step, hold, version and optional property inspection.
- `control_workflow_job` can set, run, stop, finish or assign steps only when
  both `confirm_workflow_action=true` and the separate MCP configuration
  `allow_external_workflow_actions=true` are present. The configuration is
  disabled by default because these operations change remote workflow state.

## Knowledge Graph and link charts

- `list_knowledge_graphs` inventories investigations and optionally materializes
  entity, relationship, property, search and spatial-reference data models.
- `query_knowledge_graph` runs bounded read-only openCypher queries or full-text
  searches and safely materializes primitive, object, entity, relationship,
  array and path values with depth/item limits.
- `manage_knowledge_graph_investigation` opens or renames existing
  investigations.
- `get_link_chart_state` and `configure_link_chart` inspect or configure native
  link-chart layout, root nodes and spatial/nonspatial display state without
  editing graph records.

## Topology and network layers

`inspect_network_topology_layer` recognizes and reports typed state for:

- geodatabase topology layers, including state, tolerances, feature classes and
  rules;
- utility networks, including topology state, sources, asset groups, network
  attributes, domain networks, tiers, terminals and named trace configurations;
- trace-network, Network Analyst and network-dataset layers.

Validation, topology edits, utility-network associations and traces are not
performed by this read-only action.
