using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class DataSourceActions
{
    internal static async Task<object> GetConnectionAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var memberName = BridgeSupport.String(payload, "map_member_name")
            ?? throw new ArgumentException("map_member_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            var resolved = ResolveMember(BridgeSupport.String(payload, "map_name"), memberName);
            var connection = resolved.member.GetDataConnection();
            return (object)new
            {
                map = resolved.map.Name,
                map_member = resolved.member.Name,
                member_type = resolved.member.GetType().Name,
                connection_status = resolved.member.ConnectionStatus.ToString(),
                connection_type = connection?.GetType().Name,
                cim_json = connection?.ToJson(),
            };
        });
    }

    internal static async Task<object> SetConnectionAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var memberName = BridgeSupport.String(payload, "map_member_name")
            ?? throw new ArgumentException("map_member_name is required");
        var connectionType = BridgeSupport.String(payload, "connection_type")
            ?? throw new ArgumentException("connection_type is required");
        var cimJson = BridgeSupport.String(payload, "cim_json")
            ?? throw new ArgumentException("cim_json is required");
        if (!BridgeSupport.Bool(payload, "confirm_replace_connection", false))
        {
            throw new ArgumentException("confirm_replace_connection=true is required");
        }
        return await QueuedTask.Run<object>(() =>
        {
            var resolved = ResolveMember(BridgeSupport.String(payload, "map_name"), memberName);
            CIMDataConnection connection = connectionType.ToLowerInvariant() switch
            {
                "standard" or "cimstandarddataconnection" => (CIMDataConnection)CIMStandardDataConnection.FromJson(
                    cimJson, new JsonDeserializationSettings()),
                "feature_dataset" or "cimfeaturedatasetdataconnection" =>
                    (CIMDataConnection)CIMFeatureDatasetDataConnection.FromJson(
                        cimJson, new JsonDeserializationSettings()),
                _ => throw new ArgumentException($"Unsupported data connection CIM type: {connectionType}"),
            };
            resolved.member.SetDataConnection(
                connection,
                BridgeSupport.Bool(payload, "validate_connection", true));
            return (object)new
            {
                map = resolved.map.Name,
                map_member = resolved.member.Name,
                connection_status = resolved.member.ConnectionStatus.ToString(),
                connection_type = resolved.member.GetDataConnection()?.GetType().Name,
                project_reference_changed = true,
                source_dataset_modified = false,
            };
        });
    }

    internal static async Task<object> ReplaceDataSourceAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var memberName = BridgeSupport.String(payload, "map_member_name")
            ?? throw new ArgumentException("map_member_name is required");
        var dataPath = BridgeSupport.String(payload, "data_path")
            ?? throw new ArgumentException("data_path is required");
        if (!BridgeSupport.Bool(payload, "confirm_replace_connection", false))
        {
            throw new ArgumentException("confirm_replace_connection=true is required");
        }
        return await QueuedTask.Run<object>(() =>
        {
            var resolved = ResolveMember(BridgeSupport.String(payload, "map_name"), memberName);
            var item = ItemFactory.Instance.Create(dataPath, ItemFactory.ItemType.PathItem);
            if (!ItemFactory.Instance.CanGetDataset(item))
            {
                throw new InvalidOperationException($"ArcGIS cannot open the replacement dataset: {dataPath}");
            }
            using var dataset = ItemFactory.Instance.GetDataset(item);
            switch (resolved.member)
            {
                case Layer layer:
                    if (!layer.CanReplaceDataSource(dataset))
                    {
                        throw new InvalidOperationException(
                            $"Layer cannot replace its data source with {dataset.GetType().Name}: {dataPath}");
                    }
                    layer.ReplaceDataSource(dataset);
                    break;
                case StandaloneTable standalone when dataset is Table table:
                    if (!standalone.CanReplaceDataSource(table))
                    {
                        throw new InvalidOperationException(
                            $"Standalone table cannot use replacement table: {dataPath}");
                    }
                    standalone.ReplaceDataSource(table);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported map member for data-source replacement: {resolved.member.GetType().Name}");
            }
            return (object)new
            {
                map = resolved.map.Name,
                map_member = resolved.member.Name,
                replacement_path = dataPath,
                replacement_dataset_type = dataset.GetType().Name,
                connection_status = resolved.member.ConnectionStatus.ToString(),
                project_reference_changed = true,
                source_dataset_modified = false,
            };
        });
    }

    private static (Map map, MapMember member) ResolveMember(string? mapName, string memberName)
    {
        var map = BridgeSupport.ResolveMap(mapName);
        var layer = map.GetLayersAsFlattenedList().FirstOrDefault(candidate => string.Equals(
            candidate.Name, memberName, StringComparison.OrdinalIgnoreCase));
        if (layer is not null)
        {
            return (map, layer);
        }
        var table = map.GetStandaloneTablesAsFlattenedList().FirstOrDefault(candidate => string.Equals(
            candidate.Name, memberName, StringComparison.OrdinalIgnoreCase));
        return table is not null
            ? (map, table)
            : throw new InvalidOperationException($"Map member not found: {memberName}");
    }
}
