using System.Text.Json;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class NetworkTopologyActions
{
    internal static async Task<object> InspectAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var layerName = Required(payload, "layer_name");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = BridgeSupport.ResolveLayer(map, layerName);
            object details = layer switch
            {
                TopologyLayer topology => DescribeTopology(topology),
                UtilityNetworkLayer utility => DescribeUtilityNetwork(utility),
                TraceNetworkLayer trace => new
                {
                    network_type = "TraceNetwork",
                    version = trace.TNVersion,
                    diagram_manager_type = trace.GetDiagramManager()?.GetType().Name,
                },
                NetworkAnalystLayer analyst => new
                {
                    network_type = "NetworkAnalyst",
                    solve_type = analyst.SolveType.ToString(),
                    sublayers = analyst.Layers.Select(item => item.Name).ToArray(),
                    standalone_tables = analyst.StandaloneTables.Select(item => item.Name).ToArray(),
                },
                NetworkDatasetLayer dataset => new
                {
                    network_type = "NetworkDataset",
                    map_layer_type = dataset.MapLayerType.ToString(),
                    connection_status = dataset.ConnectionStatus.ToString(),
                    path = dataset.GetPath()?.ToString(),
                },
                _ => throw new InvalidOperationException(
                    $"Layer is not a supported topology/network layer: {layer.Name} ({layer.GetType().Name})"),
            };
            var definition = layer.GetDefinition();
            return (object)new
            {
                map = map.Name,
                layer = layer.Name,
                layer_type = layer.GetType().Name,
                visible = layer.IsVisible,
                cim_type = definition.GetType().Name,
                details,
                source_data_modified = false,
            };
        });
    }

    private static object DescribeTopology(TopologyLayer layer)
    {
        using var topology = layer.GetTopology();
        var definition = topology.GetDefinition();
        var extent = topology.GetExtent();
        return new
        {
            network_type = "GeodatabaseTopology",
            state = topology.GetState().ToString(),
            cluster_tolerance = definition.GetClusterTolerance(),
            z_cluster_tolerance = definition.GetZClusterTolerance(),
            feature_classes = definition.GetFeatureClassNames(),
            rules = definition.GetRules().Select(rule => new
            {
                id = rule.ID,
                rule_type = rule.RuleType.ToString(),
                origin_class = rule.OriginClass,
                origin_subtype = rule.OriginSubtype?.ToString(),
                destination_class = rule.DestinationClass,
                destination_subtype = rule.DestinationSubtype?.ToString(),
            }).ToArray(),
            extent = DescribeExtent(extent),
        };
    }

    private static object DescribeUtilityNetwork(UtilityNetworkLayer layer)
    {
        using var network = layer.GetUtilityNetwork();
        var definition = network.GetDefinition();
        var state = network.GetState();
        return new
        {
            network_type = "UtilityNetwork",
            version = layer.UNVersion,
            schema_version = definition.GetSchemaVersion(),
            asset_group_field = definition.GetAssetGroupField(),
            asset_type_field = definition.GetAssetTypeField(),
            association_status_field = definition.GetAssociationStatusField(),
            categories = definition.GetAvailableCategories(),
            state = new
            {
                topology_enabled = state.IsNetworkTopologyEnabled,
                has_dirty_areas = state.HasDirtyAreas,
                has_errors = state.HasErrors,
                last_consistent_moment = state.LastConsistentMoment,
                last_update_is_connected_moment = state.LastUpdateIsConnectedMoment,
            },
            extent = DescribeExtent(network.GetExtent()),
            service_territory = DescribeExtent(definition.GetServiceTerritoryEnvelope()),
            network_sources = definition.GetNetworkSources().Select(source => new
            {
                id = source.ID,
                name = source.Name,
                type = source.Type.ToString(),
                usage_type = source.UsageType.ToString(),
                asset_groups = source.GetAssetGroups().Select(group => new
                {
                    code = group.Code,
                    name = group.Name,
                    asset_type_count = group.GetAssetTypes().Count,
                }).ToArray(),
            }).ToArray(),
            network_attributes = definition.GetNetworkAttributes().Select(attribute => new
            {
                name = attribute.Name,
                type = attribute.Type.ToString(),
                nullable = attribute.IsNullable,
                inline = attribute.IsInline,
                apportionable = attribute.IsApportionable,
                substitution = attribute.IsSubstitution,
                system_attribute = attribute.IsSystemAttribute,
                assignment_count = attribute.Assignments.Count,
                domain = attribute.Domain?.GetName(),
            }).ToArray(),
            domain_networks = definition.GetDomainNetworks().Select(domain => new
            {
                id = domain.ID,
                name = domain.Name,
                alias = domain.Alias,
                structure_network = domain.IsStructureNetwork,
                controller_type = domain.SubnetworkControllerType.ToString(),
                tier_definition = domain.TierDefinition.ToString(),
                network_sources = domain.NetworkSources.Select(source => source.Name).ToArray(),
                tiers = domain.Tiers.Select(tier => tier.Name).ToArray(),
                tier_groups = domain.TierGroups.Select(group => group.Name).ToArray(),
            }).ToArray(),
            terminal_configurations = definition.GetTerminalConfigurations().Select(config => new
            {
                id = config.ID,
                name = config.Name,
                directionality = config.Directionality.ToString(),
                terminals = config.Terminals.Select(terminal => terminal.Name).ToArray(),
                valid_path_count = config.ValidConfigurationPaths.Count,
            }).ToArray(),
            named_trace_configurations = layer.GetNamedTraceConfigurations().Select(config => new
            {
                global_id = config.GlobalID,
                name = config.Name,
                description = config.Description,
                creator = config.Creator,
                trace_type = config.TraceType,
                tags = config.Tags,
                result_types = config.ResultTypes.Select(item => item.ToString()).ToArray(),
                minimum_starting_locations = config.MinimumStartingLocations.ToString(),
            }).ToArray(),
        };
    }

    private static object? DescribeExtent(ArcGIS.Core.Geometry.Envelope? extent)
    {
        return extent is null ? null : new
        {
            xmin = extent.XMin,
            ymin = extent.YMin,
            xmax = extent.XMax,
            ymax = extent.YMax,
            zmin = extent.ZMin,
            zmax = extent.ZMax,
            spatial_reference = extent.SpatialReference?.Name,
        };
    }

    private static string Required(JsonElement payload, string name)
    {
        return BridgeSupport.String(payload, name)
            ?? throw new ArgumentException($"{name} is required");
    }
}
