# ArcGIS Pro 3.6.3 SDK audit for protocol v0.22

The locally installed ArcGIS Pro 3.6.3 assemblies expose more public automation
surface than the previous capability matrix recorded.

## Task Assistant

`TaskAssistantFactory` publicly supports validation, item/file opening,
item/task targeting, close, import and export. `TaskProjectItem` and
`TaskItemInfo` expose open state and task/group inventory.

## Workflow Manager

The installed `WorkflowClientModule` exposes static connection, service item,
jobs and notification managers. `IJobsManager` provides bounded search, job
inspection and step state/assignment operations. Read operations are enabled;
step mutations are independently MCP-gated because they change a remote system.

## Knowledge Graph

`KnowledgeGraphInvestigationProjectItem` opens the public Core
`KnowledgeGraph` datastore. Public data-model, search/query cursor and graph
value families support bounded schema and row materialization without COM
interfaces. Mapping extensions publicly expose link-chart layouts, root-node
sets and spatial/nonspatial display configuration.

## Topology and utility networks

`TopologyLayer.GetTopology()` and `UtilityNetworkLayer.GetUtilityNetwork()`
provide public definitions and state. Utility-network definitions expose
sources, asset groups, attributes, domain networks, tiers, terminals and named
trace configurations. Validation and editing APIs also exist but were not
enabled in this version because they update source/network topology state.

Trace-network and Network Analyst public mapping layers expose useful summary
state, while deeper trace/solve authoring remains a later extension-specific
audit.
