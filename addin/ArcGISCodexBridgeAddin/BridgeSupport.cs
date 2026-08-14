using System.Globalization;
using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class BridgeSupport
{
    internal const string ProtocolVersion = "0.28.2";

    internal static string ResolveInstallRoot()
    {
        var environmentRoot = Environment.GetEnvironmentVariable(
            "ARCGIS_CODEX_BRIDGE_INSTALL_ROOT");
        if (!string.IsNullOrWhiteSpace(environmentRoot))
        {
            return System.IO.Path.GetFullPath(environmentRoot);
        }

        var defaultRoot = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArcGISCodexBridge");
        var locatorPath = System.IO.Path.Combine(defaultRoot, "install-location.json");
        try
        {
            if (System.IO.File.Exists(locatorPath))
            {
                using var document = JsonDocument.Parse(System.IO.File.ReadAllText(locatorPath));
                if (document.RootElement.TryGetProperty("install_root", out var root)
                    && root.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(root.GetString()))
                {
                    return System.IO.Path.GetFullPath(root.GetString()!);
                }
            }
        }
        catch
        {
            // A malformed locator falls back to the standard per-user location.
        }
        return defaultRoot;
    }

    internal static string ResolveQueueRoot()
    {
        var environmentRoot = Environment.GetEnvironmentVariable("ARCGIS_CODEX_BRIDGE_QUEUE");
        if (!string.IsNullOrWhiteSpace(environmentRoot))
        {
            return System.IO.Path.GetFullPath(environmentRoot);
        }

        var installRoot = ResolveInstallRoot();
        var defaultRoot = System.IO.Path.Combine(installRoot, "queue");
        var configPath = System.IO.Path.Combine(installRoot, "config", "config.json");
        try
        {
            if (!System.IO.File.Exists(configPath))
            {
                return defaultRoot;
            }
            using var document = JsonDocument.Parse(System.IO.File.ReadAllText(configPath));
            if (document.RootElement.TryGetProperty("live_bridge_queue", out var queue)
                && queue.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(queue.GetString()))
            {
                return System.IO.Path.GetFullPath(queue.GetString()!);
            }
        }
        catch
        {
            // A malformed or unavailable private config falls back to the stable queue.
        }
        return defaultRoot;
    }

    internal static readonly string[] Capabilities =
    {
        "status",
        "project_inventory",
        "save_project",
        "get_metadata",
        "set_metadata",
        "capabilities",
        "execute_batch",
        "add_data",
        "create_map",
        "rename_map",
        "duplicate_map",
        "remove_map",
        "open_map",
        "list_bookmarks",
        "create_bookmark",
        "remove_bookmark",
        "zoom_to_bookmark",
        "configure_bookmark",
        "move_bookmark",
        "import_bookmarks",
        "export_bookmarks",
        "get_camera",
        "set_camera",
        "zoom_full_extent",
        "get_map_view_state",
        "configure_map_view",
        "get_view_filter_state",
        "configure_view_filters",
        "get_active_view_context",
        "configure_active_view_context",
        "navigate_map_view",
        "transform_view_coordinates",
        "flash_features",
        "show_feature_popup",
        "close_feature_popups",
        "manage_feature_cache",
        "capture_view_thumbnail",
        "get_map_cim",
        "set_map_cim",
        "configure_map_properties",
        "save_map_file",
        "run_geoprocessing",
        "run_arcpy_worker",
        "describe_layer",
        "get_layer_cim",
        "set_layer_cim",
        "inspect_las_layer",
        "configure_las_layer",
        "inspect_voxel_layer",
        "configure_voxel_layer",
        "get_layer_schema",
        "query_features",
        "summarize_field",
        "get_table_schema",
        "query_table",
        "select_features",
        "get_selection",
        "clear_selection",
        "update_feature_attributes",
        "create_features",
        "delete_features",
        "save_edits",
        "discard_edits",
        "undo_edit",
        "redo_edit",
        "duplicate_layer",
        "create_group_layer",
        "add_standalone_table",
        "manage_standalone_table",
        "remove_standalone_table",
        "get_standalone_table_cim",
        "set_standalone_table_cim",
        "manage_layers",
        "remove_layers",
        "reorder_layer",
        "zoom_to_layer",
        "apply_symbology",
        "apply_vector_symbology",
        "configure_visual_variables",
        "apply_raster_symbology",
        "configure_raster_rendering_rule",
        "configure_multidimensional_raster",
        "list_elevation_surfaces",
        "configure_elevation_surface",
        "configure_layer_elevation",
        "sample_elevation_surface",
        "get_elevation_profile",
        "get_animation_state",
        "control_animation",
        "begin_animation_export",
        "configure_labels",
        "list_label_classes",
        "configure_label_class",
        "remove_label_classes",
        "configure_popup",
        "configure_fields",
        "configure_display_filters",
        "configure_time",
        "get_charts",
        "set_charts",
        "draw_graphics",
        "list_map_graphics",
        "manage_map_graphics",
        "get_map_graphic_cim",
        "set_map_graphic_cim",
        "create_layout",
        "duplicate_layout",
        "rename_layout",
        "remove_layout",
        "open_layout",
        "configure_layout_page",
        "manage_layout_guides",
        "save_layout_file",
        "add_map_frame",
        "list_dynamic_frames",
        "add_table_frame",
        "add_chart_frame",
        "configure_map_frame",
        "list_extent_indicators",
        "add_extent_indicator",
        "configure_extent_indicator",
        "remove_extent_indicators",
        "configure_map_clipping",
        "add_dynamic_text",
        "create_map_series",
        "list_map_series",
        "configure_map_series",
        "remove_map_series",
        "export_map_series",
        "arrange_layout_elements",
        "export_map_view",
        "get_layout_cim",
        "set_layout_cim",
        "get_layout_element_cim",
        "set_layout_element_cim",
        "create_layout_element_from_cim",
        "update_layout_element",
        "organize_layout_elements",
        "add_layout_picture",
        "add_layout_graphics",
        "add_layout_ticks",
        "add_layout_colorbar",
        "add_raster_colorbar_from_layer",
        "import_layout",
        "set_layout_text",
        "zoom_layout",
        "export_layout",
        "save_project_as",
        "save_copy",
        "list_reports",
        "create_report",
        "configure_report",
        "get_report_cim",
        "set_report_cim",
        "get_report_element_cim",
        "set_report_element_cim",
        "duplicate_report",
        "rename_report",
        "remove_report",
        "export_report_pdf",
        "list_catalog_items",
        "add_project_item",
        "remove_project_item",
        "repair_project_item",
        "manage_favorite",
        "list_presentations",
        "create_presentation",
        "get_presentation_cim",
        "set_presentation_cim",
        "get_presentation_page_cim",
        "set_presentation_page_cim",
        "get_presentation_page_element_cim",
        "set_presentation_page_element_cim",
        "duplicate_presentation",
        "rename_presentation",
        "remove_presentation",
        "open_presentation",
        "configure_presentation",
        "add_presentation_page",
        "configure_presentation_page",
        "remove_presentation_pages",
        "export_presentation",
        "save_presentation_file",
        "list_attachments",
        "export_attachment",
        "add_attachment",
        "update_attachment",
        "remove_attachments",
        "list_relationships",
        "query_related_records",
        "list_style_items",
        "list_map_grids",
        "add_map_grid",
        "set_map_grid_cim",
        "configure_map_grid",
        "remove_map_grids",
        "list_map_surrounds",
        "add_map_surround",
        "configure_map_surround",
        "set_map_surround_cim",
        "get_data_connection",
        "set_data_connection",
        "replace_data_source",
        "spatial_query_features",
        "spatial_select_features",
        "list_task_items",
        "manage_task",
        "get_workflow_status",
        "search_workflow_jobs",
        "get_workflow_job",
        "control_workflow_job",
        "list_knowledge_graphs",
        "manage_knowledge_graph_investigation",
        "query_knowledge_graph",
        "get_link_chart_state",
        "configure_link_chart",
        "inspect_network_topology_layer",
        "get_framework_state",
        "inspect_framework_command",
        "execute_framework_command",
        "configure_framework_application",
        "manage_framework_pane",
        "get_portal_state",
        "inspect_time_zones",
        "inspect_locators",
        "geocode_location",
        "get_operation_history",
        "control_operation_history",
        "geometry_engine",
        "manage_exploratory_analysis",
        "manage_report_elements",
        "manage_presentation_elements",
        "configure_specialized_renderer",
        "inspect_printers",
        "manage_report_composition",
        "configure_presentation_transition",
        "evaluate_attribute_rules",
    };

    internal static string? String(JsonElement payload, string name, string? fallback = null)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }
        return fallback;
    }

    internal static bool Bool(JsonElement payload, string name, bool fallback = false)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }
        return fallback;
    }

    internal static double Double(JsonElement payload, string name, double fallback = 0)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number))
        {
            return number;
        }
        return fallback;
    }

    internal static int Int(JsonElement payload, string name, int fallback = 0)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number))
        {
            return number;
        }
        return fallback;
    }

    internal static string[] Strings(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
    }

    internal static double[] Doubles(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<double>();
        }
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Number)
            .Select(item => item.GetDouble())
            .ToArray();
    }

    internal static int[] Ints(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<int>();
        }
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out _))
            .Select(item => item.GetInt32())
            .ToArray();
    }

    internal static Map ResolveMap(string? requestedMap)
    {
        if (Project.Current is null)
        {
            throw new InvalidOperationException("No ArcGIS Pro project is loaded.");
        }

        Map? map = null;
        if (!string.IsNullOrWhiteSpace(requestedMap))
        {
            map = Project.Current.GetItems<MapProjectItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Name, requestedMap, StringComparison.OrdinalIgnoreCase))
                ?.GetMap();
        }
        map ??= MapView.Active?.Map;
        map ??= Project.Current.GetItems<MapProjectItem>().FirstOrDefault()?.GetMap();
        return map ?? throw new InvalidOperationException("The current project has no map.");
    }

    internal static Layer ResolveLayer(Map map, string layerName)
    {
        return map.GetLayersAsFlattenedList()
            .FirstOrDefault(layer => string.Equals(
                layer.Name, layerName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Layer not found: {layerName}");
    }

    internal static Layout ResolveLayout(string layoutName)
    {
        if (Project.Current is null)
        {
            throw new InvalidOperationException("No ArcGIS Pro project is loaded.");
        }
        return Project.Current.GetItems<LayoutProjectItem>()
            .FirstOrDefault(item => string.Equals(
                item.Name, layoutName, StringComparison.OrdinalIgnoreCase))
            ?.GetLayout()
            ?? throw new InvalidOperationException($"Layout not found: {layoutName}");
    }

    internal static Envelope Bounds(
        JsonElement payload,
        string property,
        double xmin,
        double ymin,
        double xmax,
        double ymax)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(property, out var bounds)
            && bounds.ValueKind == JsonValueKind.Object)
        {
            xmin = Double(bounds, "xmin", xmin);
            ymin = Double(bounds, "ymin", ymin);
            xmax = Double(bounds, "xmax", xmax);
            ymax = Double(bounds, "ymax", ymax);
        }
        return EnvelopeBuilderEx.CreateEnvelope(xmin, ymin, xmax, ymax);
    }

    internal static CIMColor Color(JsonElement value, CIMColor? fallback = null)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            var channels = value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Number)
                .Select(item => item.GetDouble())
                .ToArray();
            if (channels.Length >= 3)
            {
                return ColorFactory.Instance.CreateRGBColor(
                    channels[0], channels[1], channels[2], channels.Length >= 4 ? channels[3] : 100);
            }
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()?.Trim().TrimStart('#');
            if (text is { Length: 6 or 8 }
                && int.TryParse(text[..2], NumberStyles.HexNumber, null, out var r)
                && int.TryParse(text.Substring(2, 2), NumberStyles.HexNumber, null, out var g)
                && int.TryParse(text.Substring(4, 2), NumberStyles.HexNumber, null, out var b))
            {
                var alpha = text.Length == 8
                    && int.TryParse(text.Substring(6, 2), NumberStyles.HexNumber, null, out var a)
                        ? a / 255.0 * 100.0
                        : 100.0;
                return ColorFactory.Instance.CreateRGBColor(r, g, b, alpha);
            }
        }

        return fallback ?? ColorFactory.Instance.CreateRGBColor(80, 80, 80);
    }

    internal static CIMColor Color(
        JsonElement payload,
        string property,
        CIMColor? fallback = null)
    {
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(property, out var value)
            ? Color(value, fallback)
            : fallback ?? ColorFactory.Instance.CreateRGBColor(80, 80, 80);
    }

    internal static CIMColorRamp ColorRamp(JsonElement payload, string property = "color_stops")
    {
        var colors = new List<CIMColor>();
        var positions = new List<double>();
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(property, out var stops)
            && stops.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in stops.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("color", out var color))
                {
                    colors.Add(Color(color));
                    if (item.TryGetProperty("value", out var value)
                        && value.ValueKind == JsonValueKind.Number)
                    {
                        positions.Add(value.GetDouble());
                    }
                    else if (item.TryGetProperty("position", out var position)
                        && position.ValueKind == JsonValueKind.Number)
                    {
                        positions.Add(position.GetDouble());
                    }
                }
                else
                {
                    colors.Add(Color(item));
                }
            }
        }

        if (colors.Count == 0)
        {
            colors.Add(ColorFactory.Instance.CreateRGBColor(245, 242, 225));
            colors.Add(ColorFactory.Instance.CreateRGBColor(46, 107, 79));
        }
        if (colors.Count == 1)
        {
            colors.Add(colors[0]);
        }
        if (colors.Count == 2)
        {
            return ColorFactory.Instance.ConstructColorRamp(
                ColorRampAlgorithm.LinearContinuous, colors[0], colors[1]);
        }

        var ramps = new List<CIMColorRamp>();
        for (var index = 0; index < colors.Count - 1; index++)
        {
            ramps.Add(ColorFactory.Instance.ConstructColorRamp(
                ColorRampAlgorithm.LinearContinuous, colors[index], colors[index + 1]));
        }
        var weights = Enumerable.Repeat(1.0, ramps.Count).ToArray();
        if (positions.Count == colors.Count
            && positions.Zip(positions.Skip(1), (left, right) => right > left).All(result => result))
        {
            weights = positions.Zip(positions.Skip(1), (left, right) => right - left).ToArray();
        }
        return new CIMMultipartColorRamp
        {
            ColorRamps = ramps.ToArray(),
            Weights = weights,
        };
    }

    internal static ClassificationMethod Classification(string? value)
    {
        if (Enum.TryParse<ClassificationMethod>(value, true, out var parsed))
        {
            return parsed;
        }
        return (value ?? string.Empty).ToLowerInvariant() switch
        {
            "natural_breaks" or "jenks" => ClassificationMethod.NaturalBreaks,
            "equal_interval" => ClassificationMethod.EqualInterval,
            "quantile" => ClassificationMethod.Quantile,
            "standard_deviation" => ClassificationMethod.StandardDeviation,
            "geometric_interval" => ClassificationMethod.GeometricalInterval,
            _ => ClassificationMethod.NaturalBreaks,
        };
    }

    internal static RasterStretchType Stretch(string? value)
    {
        if (Enum.TryParse<RasterStretchType>(value, true, out var parsed))
        {
            return parsed;
        }
        return (value ?? string.Empty).ToLowerInvariant() switch
        {
            "minmax" or "minimum_maximum" => RasterStretchType.MinimumMaximum,
            "percent_clip" or "percent_minimum_maximum" => RasterStretchType.PercentMinimumMaximum,
            "standard_deviation" or "standard_deviations" => RasterStretchType.StandardDeviations,
            "histogram_equalize" => RasterStretchType.HistogramEqualize,
            _ => RasterStretchType.DefaultFromSource,
        };
    }

    internal static CIMSymbol FeatureSymbol(BasicFeatureLayer layer, JsonElement config)
    {
        var color = Color(config, "color", ColorFactory.Instance.CreateRGBColor(60, 110, 160, 70));
        var outline = Color(config, "outline_color", ColorFactory.Instance.CreateRGBColor(40, 40, 40));
        var width = Double(config, "width", 1.2);
        var size = Double(config, "size", 6.0);

        if (layer.ShapeType is esriGeometryType.esriGeometryPoint
            or esriGeometryType.esriGeometryMultipoint)
        {
            var style = Enum.TryParse<SimpleMarkerStyle>(String(config, "marker", "Circle"), true, out var marker)
                ? marker
                : SimpleMarkerStyle.Circle;
            return SymbolFactory.Instance.ConstructPointSymbol(color, size, style);
        }
        if (layer.ShapeType is esriGeometryType.esriGeometryPolyline
            or esriGeometryType.esriGeometryLine)
        {
            var style = Enum.TryParse<SimpleLineStyle>(String(config, "line_style", "Solid"), true, out var line)
                ? line
                : SimpleLineStyle.Solid;
            return SymbolFactory.Instance.ConstructLineSymbol(color, width, style);
        }

        var strokeStyle = Enum.TryParse<SimpleLineStyle>(String(config, "outline_style", "Solid"), true, out var stroke)
            ? stroke
            : SimpleLineStyle.Solid;
        return SymbolFactory.Instance.ConstructPolygonSymbol(
            color,
            SimpleFillStyle.Solid,
            SymbolFactory.Instance.ConstructStroke(outline, width, strokeStyle));
    }

    internal static CIMTextSymbol TextSymbol(JsonElement config)
    {
        var symbol = SymbolFactory.Instance.ConstructTextSymbol(
            Color(config, "color", ColorFactory.Instance.CreateRGBColor(35, 35, 35)),
            Double(config, "size", 9),
            String(config, "font_family", "Microsoft YaHei")!,
            String(config, "font_style", "Regular")!);
        if (Enum.TryParse<HorizontalAlignment>(String(config, "horizontal_alignment"), true, out var horizontal))
        {
            symbol.HorizontalAlignment = horizontal;
        }
        if (Enum.TryParse<VerticalAlignment>(String(config, "vertical_alignment"), true, out var vertical))
        {
            symbol.VerticalAlignment = vertical;
        }
        return symbol;
    }

    internal static string FormatCoordinate(double value, bool geographic, bool longitude)
    {
        if (!geographic)
        {
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }
        var suffix = longitude
            ? value >= 0 ? "E" : "W"
            : value >= 0 ? "N" : "S";
        return $"{Math.Abs(value):0.00}°{suffix}";
    }
}
