using System.Text.Json;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGIS.Desktop.Reports;

namespace ArcGISCodexBridgeAddin;

internal static class MetadataActions
{
    internal static async Task<object> GetAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var targetType = BridgeSupport.String(payload, "target_type", "project")!.ToLowerInvariant();
        var targetName = BridgeSupport.String(payload, "target_name");
        var maximumCharacters = Math.Clamp(
            BridgeSupport.Int(payload, "max_characters", 100000),
            0,
            1000000);
        return await QueuedTask.Run<object>(() =>
        {
            string xml;
            string resolvedName;
            switch (targetType)
            {
                case "project":
                    xml = Project.Current.GetXml();
                    resolvedName = Project.Current.Name;
                    break;
                case "map":
                    var map = BridgeSupport.ResolveMap(targetName);
                    xml = map.GetMetadata();
                    resolvedName = map.Name;
                    break;
                case "layer":
                    var layerMap = BridgeSupport.ResolveMap(BridgeSupport.String(payload, "map_name"));
                    var layer = BridgeSupport.ResolveLayer(
                        layerMap,
                        targetName ?? throw new ArgumentException("target_name is required for a layer"));
                    xml = layer.GetMetadata();
                    resolvedName = layer.Name;
                    break;
                case "layout":
                    var layout = BridgeSupport.ResolveLayout(
                        targetName ?? throw new ArgumentException("target_name is required for a layout"));
                    xml = layout.GetMetadata();
                    resolvedName = layout.Name;
                    break;
                case "report":
                    var reportItem = Project.Current.GetItems<ReportProjectItem>()
                        .FirstOrDefault(item => string.Equals(
                            item.Name, targetName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Report not found: {targetName}");
                    xml = reportItem.GetXml();
                    resolvedName = reportItem.Name;
                    break;
                default:
                    throw new ArgumentException($"Unsupported metadata target type: {targetType}");
            }
            xml ??= string.Empty;
            var returned = maximumCharacters == 0
                ? string.Empty
                : xml[..Math.Min(xml.Length, maximumCharacters)];
            return (object)new
            {
                target_type = targetType,
                target_name = resolvedName,
                character_count = xml.Length,
                returned_character_count = returned.Length,
                truncated = returned.Length < xml.Length,
                metadata_xml = returned,
            };
        });
    }

    internal static async Task<object> SetAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!BridgeSupport.Bool(payload, "confirm_write", false))
        {
            throw new ArgumentException("confirm_write=true is required to change metadata");
        }
        var targetType = BridgeSupport.String(payload, "target_type", "project")!.ToLowerInvariant();
        var targetName = BridgeSupport.String(payload, "target_name");
        var xml = BridgeSupport.String(payload, "metadata_xml")
            ?? throw new ArgumentException("metadata_xml is required");
        return await QueuedTask.Run<object>(() =>
        {
            string resolvedName;
            switch (targetType)
            {
                case "project":
                    Project.Current.SetXml(xml);
                    resolvedName = Project.Current.Name;
                    break;
                case "map":
                    var map = BridgeSupport.ResolveMap(targetName);
                    map.SetMetadata(xml);
                    resolvedName = map.Name;
                    break;
                case "layout":
                    var layout = BridgeSupport.ResolveLayout(
                        targetName ?? throw new ArgumentException("target_name is required for a layout"));
                    layout.SetMetadata(xml);
                    resolvedName = layout.Name;
                    break;
                case "report":
                    var reportItem = Project.Current.GetItems<ReportProjectItem>()
                        .FirstOrDefault(item => string.Equals(
                            item.Name, targetName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Report not found: {targetName}");
                    reportItem.SetXml(xml);
                    resolvedName = reportItem.Name;
                    break;
                case "layer":
                    throw new InvalidOperationException(
                        "Layer metadata writes are intentionally disabled because they may update source metadata.");
                default:
                    throw new ArgumentException($"Unsupported metadata target type: {targetType}");
            }
            return (object)new
            {
                target_type = targetType,
                target_name = resolvedName,
                metadata_updated = true,
                source_dataset_metadata_modified = false,
            };
        });
    }
}
