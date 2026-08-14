using System.IO;
using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class MapSeriesActions
{
    internal static async Task<object> ListAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        return await QueuedTask.Run<object>(() =>
        {
            var requestedLayout = BridgeSupport.String(payload, "layout_name");
            var layouts = Project.Current.GetItems<LayoutProjectItem>()
                .Where(item => string.IsNullOrWhiteSpace(requestedLayout)
                    || string.Equals(item.Name, requestedLayout, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.GetLayout())
                .ToArray();
            if (!string.IsNullOrWhiteSpace(requestedLayout) && layouts.Length == 0)
            {
                throw new InvalidOperationException($"Layout not found: {requestedLayout}");
            }
            return (object)new
            {
                map_series = layouts.Select(layout => Describe(layout, layout.MapSeries)).ToArray(),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> ConfigureAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var series = layout.MapSeries
                ?? throw new InvalidOperationException($"Layout has no map series: {layoutName}");
            var frameName = BridgeSupport.String(payload, "map_frame_name");
            if (!string.IsNullOrWhiteSpace(frameName))
            {
                series.MapFrame = layout.FindElement(frameName) as MapFrame
                    ?? throw new InvalidOperationException($"Map frame not found: {frameName}");
            }
            if (payload.TryGetProperty("enabled", out _))
                series.Enabled = BridgeSupport.Bool(payload, "enabled", true);
            if (payload.TryGetProperty("starting_page_number", out _))
                series.StartingPageNumber = Math.Max(1, BridgeSupport.Int(payload, "starting_page_number", 1));

            if (series is SpatialMapSeries spatial)
            {
                var indexLayerName = BridgeSupport.String(payload, "index_layer_name");
                if (!string.IsNullOrWhiteSpace(indexLayerName))
                {
                    spatial.IndexLayer = BridgeSupport.ResolveLayer(spatial.MapFrame.Map, indexLayerName) as BasicFeatureLayer
                        ?? throw new InvalidOperationException($"Index feature layer not found: {indexLayerName}");
                }
                SetString(payload, "page_name_field", value => spatial.PageNameField = value);
                SetString(payload, "page_number_field", value => spatial.PageNumberField = value);
                SetString(payload, "sort_field", value => spatial.SortField = value);
                SetString(payload, "scale_field", value => spatial.ScaleField = value);
                SetString(payload, "rotation_field", value => spatial.RotationField = value);
                SetString(payload, "spatial_reference_field", value => spatial.SpatialReferenceField = value);
                SetString(payload, "category_field", value => spatial.CategoryField = value);
                if (payload.TryGetProperty("sort_ascending", out _))
                    spatial.SortAscending = BridgeSupport.Bool(payload, "sort_ascending", true);
                if (payload.TryGetProperty("extent_options", out _))
                    spatial.ExtentOptions = ParseExtentFitType(BridgeSupport.String(payload, "extent_options"));
                if (payload.TryGetProperty("margin", out _))
                    spatial.Margin = Math.Max(0, BridgeSupport.Double(payload, "margin"));
                if (payload.TryGetProperty("margin_type", out _))
                    spatial.MarginType = ParseUnitType(BridgeSupport.String(payload, "margin_type"));
                if (payload.TryGetProperty("margin_units", out _))
                    spatial.MarginUnits = ParseLinearUnit(BridgeSupport.String(payload, "margin_units"));
                if (payload.TryGetProperty("scale_rounding", out _))
                    spatial.ScaleRounding = Math.Max(0, BridgeSupport.Double(payload, "scale_rounding"));
            }

            layout.RefreshMapSeries();
            var currentPage = BridgeSupport.String(payload, "current_page_number");
            if (!string.IsNullOrWhiteSpace(currentPage))
            {
                series.SetCurrentPageNumber(currentPage);
            }
            return Describe(layout, series);
        });
    }

    internal static async Task<object> RemoveAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        if (!BridgeSupport.Bool(payload, "confirm_remove", false))
        {
            throw new ArgumentException("confirm_remove=true is required to remove a map series");
        }
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var previous = layout.MapSeries;
            layout.SetMapSeries(null!);
            return (object)new
            {
                layout = layout.Name,
                removed = previous is not null,
                previous_series_type = previous?.GetType().Name,
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> ExportAsync(
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
            var series = layout.MapSeries
                ?? throw new InvalidOperationException($"Layout has no map series: {layoutName}");
            var format = ExportSupport.CreateFormat(output, payload);
            var options = new MapSeriesExportOptions
            {
                ExportPages = ParseExportPages(BridgeSupport.String(payload, "export_pages", "All")),
                CustomPages = BridgeSupport.String(payload, "custom_pages"),
                ExportFileOptions = ParseExportFileOptions(
                    BridgeSupport.String(payload, "file_options", "ExportAsSinglePDF")),
                ShowSelectedSymbology = BridgeSupport.Bool(payload, "show_selected_symbology", false),
                DoOrderPagesByGrouping = BridgeSupport.Bool(payload, "order_pages_by_grouping", false),
            };
            layout.Export(format, options);
            return (object)new
            {
                layout = layout.Name,
                series_type = series.GetType().Name,
                output,
                export_pages = options.ExportPages.ToString(),
                custom_pages = options.CustomPages,
                file_options = options.ExportFileOptions.ToString(),
                dpi = format.Resolution,
                derived_output_created = true,
                source_data_modified = false,
            };
        });
    }

    private static object Describe(Layout layout, MapSeries? series)
    {
        if (series is null)
        {
            return new { layout = layout.Name, present = false };
        }
        object? spatial = null;
        if (series is SpatialMapSeries item)
        {
            spatial = new
            {
                index_layer = item.IndexLayer?.Name,
                page_name_field = item.PageNameField,
                page_number_field = item.PageNumberField,
                sort_field = item.SortField,
                sort_ascending = item.SortAscending,
                scale_field = item.ScaleField,
                rotation_field = item.RotationField,
                spatial_reference_field = item.SpatialReferenceField,
                category_field = item.CategoryField,
                extent_options = item.ExtentOptions.ToString(),
                margin = item.Margin,
                margin_type = item.MarginType.ToString(),
                margin_units = item.MarginUnits?.Name,
                scale_rounding = item.ScaleRounding,
            };
        }
        return new
        {
            layout = layout.Name,
            present = true,
            series_type = series.GetType().Name,
            enabled = series.Enabled,
            map_frame = series.MapFrame?.Name,
            starting_page_number = series.StartingPageNumber,
            page_count = series.PageCount,
            current_page_name = series.CurrentPageName,
            current_page_number = series.CurrentPageNumber,
            first_page_number = series.FirstPageNumber,
            last_page_number = series.LastPageNumber,
            previous_page_number = series.PreviousPageNumber,
            next_page_number = series.NextPageNumber,
            spatial,
            source_data_modified = false,
        };
    }

    private static void SetString(JsonElement payload, string name, Action<string> setter)
    {
        if (payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            setter(value.GetString() ?? string.Empty);
        }
    }

    private static ExtentFitType ParseExtentFitType(string? value)
    {
        return Enum.TryParse<ExtentFitType>(value, true, out var parsed)
            ? parsed
            : throw new ArgumentException($"Unsupported extent fit type: {value}");
    }

    private static ArcGIS.Core.CIM.UnitType ParseUnitType(string? value)
    {
        return Enum.TryParse<ArcGIS.Core.CIM.UnitType>(value, true, out var parsed)
            ? parsed
            : throw new ArgumentException($"Unsupported margin unit type: {value}");
    }

    private static LinearUnit ParseLinearUnit(string? value)
    {
        return (value ?? string.Empty).ToLowerInvariant() switch
        {
            "meters" or "meter" or "m" => LinearUnit.Meters,
            "miles" or "mile" or "mi" => LinearUnit.Miles,
            "feet" or "foot" or "ft" => LinearUnit.Feet,
            "centimeters" or "centimeter" or "cm" => LinearUnit.Centimeters,
            "inches" or "inch" or "in" => LinearUnit.Inches,
            _ => LinearUnit.Kilometers,
        };
    }

    private static ExportPages ParseExportPages(string? value)
    {
        return Enum.TryParse<ExportPages>(value, true, out var parsed)
            ? parsed
            : throw new ArgumentException($"Unsupported map-series export pages option: {value}");
    }

    private static ExportFileOptions ParseExportFileOptions(string? value)
    {
        return Enum.TryParse<ExportFileOptions>(value, true, out var parsed)
            ? parsed
            : throw new ArgumentException($"Unsupported map-series file option: {value}");
    }
}
