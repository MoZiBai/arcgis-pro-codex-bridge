using System.IO;
using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;

namespace ArcGISCodexBridgeAddin;

internal static class ProjectTemplateActions
{
    internal static async Task<object> ConfigureMapPropertiesAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(BridgeSupport.String(payload, "map_name"));
            var definition = map.GetDefinition();
            if (payload.TryGetProperty("reference_scale", out _))
                definition.ReferenceScale = Math.Max(0, BridgeSupport.Double(payload, "reference_scale"));
            if (payload.TryGetProperty("background_color", out _))
                definition.BackgroundColor = BridgeSupport.Color(payload, "background_color");
            if (payload.TryGetProperty("surface_color", out _))
                definition.SurfaceColor = BridgeSupport.Color(payload, "surface_color");
            if (payload.TryGetProperty("enable_wraparound", out _))
                definition.EnableWraparound = BridgeSupport.Bool(payload, "enable_wraparound", false);
            if (payload.TryGetProperty("snap_to_scales", out _))
                definition.SnapToScales = BridgeSupport.Bool(payload, "snap_to_scales", false);
            if (payload.TryGetProperty("simulate_overprint", out _))
                definition.SimulateOverprint = BridgeSupport.Bool(payload, "simulate_overprint", false);
            if (payload.TryGetProperty("auto_fill_feature_cache", out _))
                definition.AutoFillFeatureCache = BridgeSupport.Bool(payload, "auto_fill_feature_cache", true);
            if (payload.TryGetProperty("use_service_layer_ids", out _))
                definition.UseServiceLayerIDs = BridgeSupport.Bool(payload, "use_service_layer_ids", false);
            if (payload.TryGetProperty("include_maximum_in_scale_ranges", out _))
                definition.IncludeMaximumInScaleRanges = BridgeSupport.Bool(
                    payload, "include_maximum_in_scale_ranges", false);
            if (payload.TryGetProperty("default_scale", out _))
                definition.DefaultScale = Math.Max(0, BridgeSupport.Double(payload, "default_scale"));
            if (payload.TryGetProperty("default_rotation", out _))
                definition.DefaultRotation = BridgeSupport.Double(payload, "default_rotation");
            if (payload.TryGetProperty("scales", out var scales))
            {
                if (scales.ValueKind != JsonValueKind.Array)
                {
                    throw new ArgumentException("scales must be an array");
                }
                definition.Scales = scales.EnumerateArray().Select((item, index) =>
                {
                    if (item.ValueKind == JsonValueKind.Number)
                    {
                        return new CIMScale
                        {
                            Value = Math.Max(1, item.GetDouble()),
                            Alias = $"1:{item.GetDouble():0}",
                        };
                    }
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        var value = Math.Max(1, BridgeSupport.Double(item, "value"));
                        return new CIMScale
                        {
                            Value = value,
                            Alias = BridgeSupport.String(item, "alias", $"1:{value:0}"),
                        };
                    }
                    throw new ArgumentException($"Invalid scale entry at index {index}");
                }).ToArray();
            }
            map.SetDefinition(definition);
            return (object)new
            {
                map = map.Name,
                reference_scale = definition.ReferenceScale,
                enable_wraparound = definition.EnableWraparound,
                snap_to_scales = definition.SnapToScales,
                simulate_overprint = definition.SimulateOverprint,
                auto_fill_feature_cache = definition.AutoFillFeatureCache,
                use_service_layer_ids = definition.UseServiceLayerIDs,
                include_maximum_in_scale_ranges = definition.IncludeMaximumInScaleRanges,
                default_scale = definition.DefaultScale,
                default_rotation = definition.DefaultRotation,
                scales = (definition.Scales ?? Array.Empty<CIMScale>()).Select(scale => new
                {
                    value = scale.Value,
                    alias = scale.Alias,
                }).ToArray(),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> ConfigureLayoutPageAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var page = layout.GetPage();
            if (payload.TryGetProperty("units", out _))
                page.Units = ParsePageUnit(BridgeSupport.String(payload, "units"));
            if (payload.TryGetProperty("width", out _))
                page.Width = Math.Max(0.01, BridgeSupport.Double(payload, "width"));
            if (payload.TryGetProperty("height", out _))
                page.Height = Math.Max(0.01, BridgeSupport.Double(payload, "height"));
            if (payload.TryGetProperty("show_guides", out _))
                page.ShowGuides = BridgeSupport.Bool(payload, "show_guides", false);
            if (payload.TryGetProperty("show_margin", out _))
                page.ShowMargin = BridgeSupport.Bool(payload, "show_margin", false);
            if (payload.TryGetProperty("show_rulers", out _))
                page.ShowRulers = BridgeSupport.Bool(payload, "show_rulers", true);
            if (payload.TryGetProperty("smallest_ruler_division", out _))
                page.SmallestRulerDivision = Math.Max(
                    0.0001, BridgeSupport.Double(payload, "smallest_ruler_division"));
            page.StretchElements = BridgeSupport.Bool(
                payload, "stretch_elements", page.StretchElements);
            layout.SetPage(page, BridgeSupport.Bool(payload, "resize_elements", false));
            return (object)new
            {
                layout = layout.Name,
                width = page.Width,
                height = page.Height,
                units = page.Units?.Name,
                show_guides = page.ShowGuides,
                show_margin = page.ShowMargin,
                show_rulers = page.ShowRulers,
                smallest_ruler_division = page.SmallestRulerDivision,
                stretch_elements = page.StretchElements,
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> ManageLayoutGuidesAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var operation = BridgeSupport.String(payload, "operation", "inspect")!.ToLowerInvariant();
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var page = layout.GetPage();
            var guides = (page.Guides ?? Array.Empty<CIMGuide>()).ToList();
            switch (operation)
            {
                case "inspect":
                    break;
                case "add":
                    guides.AddRange(ParseGuides(payload));
                    page.Guides = guides.ToArray();
                    page.ShowGuides = true;
                    layout.SetPage(page);
                    break;
                case "replace":
                    page.Guides = ParseGuides(payload);
                    page.ShowGuides = page.Guides.Length > 0;
                    layout.SetPage(page);
                    break;
                case "remove":
                {
                    var indices = ParseGuideIndices(payload, guides.Count);
                    foreach (var index in indices.OrderByDescending(item => item))
                        guides.RemoveAt(index);
                    page.Guides = guides.ToArray();
                    layout.SetPage(page);
                    break;
                }
                case "clear":
                    if (!BridgeSupport.Bool(payload, "confirm_clear", false))
                        throw new InvalidOperationException("confirm_clear=true is required");
                    page.Guides = Array.Empty<CIMGuide>();
                    layout.SetPage(page);
                    break;
                default:
                    throw new ArgumentException("operation must be inspect, add, replace, remove, or clear");
            }
            return (object)new
            {
                layout = layout.Name,
                operation,
                show_guides = page.ShowGuides,
                page_units = page.Units?.Name,
                guides = (page.Guides ?? Array.Empty<CIMGuide>()).Select((guide, index) => new
                {
                    index,
                    orientation = guide.Orientation.ToString(),
                    position = guide.Position,
                }).ToArray(),
                project_state_modified = operation != "inspect",
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> SaveMapFileAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var output = BridgeSupport.String(payload, "output_file")
            ?? throw new ArgumentException("output_file is required");
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? string.Empty);
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(BridgeSupport.String(payload, "map_name"));
            var overwrite = BridgeSupport.Bool(payload, "overwrite", false);
            map.SaveAsFile(output, overwrite);
            return (object)new
            {
                map = map.Name,
                output,
                overwrite,
                derived_output_created = true,
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> SaveLayoutFileAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var output = BridgeSupport.String(payload, "output_file")
            ?? throw new ArgumentException("output_file is required");
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? string.Empty);
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var overwrite = BridgeSupport.Bool(payload, "overwrite", false);
            layout.SaveAsFile(output, overwrite);
            return (object)new
            {
                layout = layout.Name,
                output,
                overwrite,
                derived_output_created = true,
                source_data_modified = false,
            };
        });
    }

    private static LinearUnit ParsePageUnit(string? value)
    {
        return (value ?? string.Empty).ToLowerInvariant() switch
        {
            "inches" or "inch" or "in" => LinearUnit.Inches,
            "millimeters" or "millimeter" or "mm" => LinearUnit.Millimeters,
            "points" or "point" or "pt" => LinearUnit.Points,
            _ => LinearUnit.Centimeters,
        };
    }

    private static CIMGuide[] ParseGuides(JsonElement payload)
    {
        if (!payload.TryGetProperty("guides", out var values) || values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("guides must be an array");
        var result = values.EnumerateArray().Take(501).Select((item, index) =>
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new ArgumentException($"Invalid guide at index {index}");
            var rawOrientation = BridgeSupport.String(item, "orientation")
                ?? throw new ArgumentException($"Guide orientation is required at index {index}");
            if (!Enum.TryParse<Orientation>(rawOrientation, true, out var orientation))
                throw new ArgumentException($"Guide orientation must be Horizontal or Vertical at index {index}");
            if (!item.TryGetProperty("position", out var position) || position.ValueKind != JsonValueKind.Number)
                throw new ArgumentException($"Guide position must be numeric at index {index}");
            return new CIMGuide
            {
                Orientation = orientation,
                Position = position.GetDouble(),
            };
        }).ToArray();
        if (result.Length > 500)
            throw new ArgumentException("guides is limited to 500 entries");
        return result;
    }

    private static int[] ParseGuideIndices(JsonElement payload, int guideCount)
    {
        if (!payload.TryGetProperty("guide_indices", out var values) || values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("guide_indices must be an array");
        var result = values.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var index))
                throw new ArgumentException("guide_indices must contain integers");
            if (index < 0 || index >= guideCount)
                throw new ArgumentOutOfRangeException(nameof(payload), $"Guide index is out of range: {index}");
            return index;
        }).Distinct().ToArray();
        if (result.Length == 0)
            throw new ArgumentException("guide_indices must contain at least one index");
        return result;
    }
}
