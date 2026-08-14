using System.IO;
using System.Text.Json;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class MapViewExportActions
{
    internal static async Task<object> ExportAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var output = BridgeSupport.String(payload, "output_file")
            ?? throw new ArgumentException("output_file is required");
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? string.Empty);
        return await QueuedTask.Run<object>(() =>
        {
            var view = MapView.Active
                ?? throw new InvalidOperationException("No active ArcGIS Pro map view is available.");
            var requestedMap = BridgeSupport.String(payload, "map_name");
            if (!string.IsNullOrWhiteSpace(requestedMap)
                && !string.Equals(view.Map.Name, requestedMap, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The requested map is not the active map view: {requestedMap}");
            }
            var format = ExportSupport.CreateFormat(output, payload);
            if (payload.TryGetProperty("width", out _))
                format.Width = Math.Max(1, BridgeSupport.Double(payload, "width"));
            if (payload.TryGetProperty("height", out _))
                format.Height = Math.Max(1, BridgeSupport.Double(payload, "height"));
            view.Export(format);
            return (object)new
            {
                map = view.Map.Name,
                output,
                dpi = format.Resolution,
                width = format.Width,
                height = format.Height,
                derived_output_created = true,
                source_data_modified = false,
            };
        });
    }
}
