using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class DynamicFrameActions
{
    internal static async Task<object> ListAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var includeCim = BridgeSupport.Bool(payload, "include_cim", false);
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var frames = layout.GetElementsAsFlattenedList()
                .Where(element => element is TableFrame or ChartFrame)
                .Select(element =>
                {
                    var definition = element.GetDefinition();
                    string? mapFrame = null;
                    string? mapMemberUri = null;
                    string? chartName = null;
                    string[]? fields = null;
                    if (definition is CIMTableFrame table)
                    {
                        mapFrame = table.MapFrame;
                        mapMemberUri = table.MapMemberURI;
                        fields = (table.Fields ?? Array.Empty<CIMTableFrameField>())
                            .Where(field => field.IsVisible)
                            .OrderBy(field => field.FieldOrder)
                            .Select(field => field.Name)
                            .ToArray();
                    }
                    else if (definition is CIMChartFrame chart)
                    {
                        mapFrame = chart.MapFrame;
                        mapMemberUri = chart.MapMemberURI;
                        chartName = chart.ChartName;
                    }
                    return new
                    {
                        name = element.Name,
                        frame_type = element.GetType().Name,
                        map_frame = mapFrame,
                        map_member_uri = mapMemberUri,
                        chart_name = chartName,
                        fields,
                        x = element.GetX(),
                        y = element.GetY(),
                        width = element.GetWidth(),
                        height = element.GetHeight(),
                        visible = element.IsVisible,
                        cim_json = includeCim ? definition.ToJson() : null,
                    };
                }).ToArray();
            return (object)new { layout = layout.Name, dynamic_frames = frames };
        });
    }

    internal static async Task<object> AddTableFrameAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var mapMemberName = BridgeSupport.String(payload, "map_member_name")
            ?? throw new ArgumentException("map_member_name is required");
        var elementName = BridgeSupport.String(payload, "element_name", "动态表格")!;
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            EnsureElementNameAvailable(layout, elementName);
            var mapFrame = ResolveMapFrame(layout, BridgeSupport.String(payload, "map_frame_name"));
            var member = ResolveMapMember(mapFrame.Map, mapMemberName);
            var available = FieldDescriptions(member);
            var requestedFields = BridgeSupport.Strings(payload, "field_names");
            var fieldNames = requestedFields.Length > 0
                ? requestedFields
                : available.Select(field => field.Name).ToArray();
            var unknown = fieldNames.Where(name => available.All(field => !string.Equals(
                field.Name, name, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (unknown.Length > 0)
            {
                throw new ArgumentException($"Unknown table-frame fields: {string.Join(", ", unknown)}");
            }

            var info = new TableFrameInfo
            {
                MapFrameName = mapFrame.Name,
                MapMemberUri = member.URI,
                FieldNames = fieldNames,
            };
            var style = ResolveTableFrameStyle(payload);
            if (style is not null)
            {
                info.TableFrameStyleItem = style;
            }
            var bounds = BridgeSupport.Bounds(payload, "bounds", 1, 1, 12, 7);
            var frame = ElementFactory.Instance.CreateMapSurroundElement(
                layout, bounds, info, elementName, false, null) as TableFrame
                ?? throw new InvalidOperationException("ArcGIS Pro did not create a table frame.");
            var definition = (CIMTableFrame)frame.GetDefinition();
            if (payload.TryGetProperty("show_headings", out _))
            {
                definition.ShowHeadings = BridgeSupport.Bool(payload, "show_headings", true);
            }
            if (payload.TryGetProperty("row_limit", out _))
            {
                definition.RowLimit = Math.Clamp(BridgeSupport.Int(payload, "row_limit", 50), 1, 100000);
            }
            if (payload.TryGetProperty("columns", out _))
            {
                definition.Columns = Math.Clamp(BridgeSupport.Int(payload, "columns", 1), 1, 100);
            }
            if (payload.TryGetProperty("balance_columns", out _))
            {
                definition.BalanceColumns = BridgeSupport.Bool(payload, "balance_columns", false);
            }
            if (payload.TryGetProperty("where_clause", out _))
            {
                definition.CustomWhereClause = BridgeSupport.String(payload, "where_clause", string.Empty);
                definition.FillingStrategy = TableFrameFillingStrategy.CustomWhereClause;
            }
            else if (Enum.TryParse<TableFrameFillingStrategy>(
                BridgeSupport.String(payload, "filling_strategy"), true, out var filling))
            {
                definition.FillingStrategy = filling;
            }
            if (Enum.TryParse<TableFrameFittingStrategy>(
                BridgeSupport.String(payload, "fitting_strategy"), true, out var fitting))
            {
                definition.FittingStrategy = fitting;
            }
            frame.SetDefinition(definition);
            return (object)new
            {
                layout = layout.Name,
                element = frame.Name,
                frame_type = frame.GetType().Name,
                map_frame = mapFrame.Name,
                map_member = member.Name,
                fields = fieldNames,
                style_item = style?.Name,
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> AddChartFrameAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var mapMemberName = BridgeSupport.String(payload, "map_member_name")
            ?? throw new ArgumentException("map_member_name is required");
        var chartName = BridgeSupport.String(payload, "chart_name")
            ?? throw new ArgumentException("chart_name is required");
        var elementName = BridgeSupport.String(payload, "element_name", chartName)!;
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            EnsureElementNameAvailable(layout, elementName);
            var mapFrame = ResolveMapFrame(layout, BridgeSupport.String(payload, "map_frame_name"));
            var member = ResolveMapMember(mapFrame.Map, mapMemberName);
            if (member is FeatureLayer featureLayer)
            {
                var definition = (CIMFeatureLayer)featureLayer.GetDefinition();
                if (!(definition.Charts ?? Array.Empty<CIMChart>()).Any(chart => string.Equals(
                    chart.Name, chartName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"Chart not found on layer {member.Name}: {chartName}");
                }
            }
            var bounds = BridgeSupport.Bounds(payload, "bounds", 1, 1, 12, 8);
            var frame = ElementFactory.Instance.CreateMapSurroundElement(
                layout,
                bounds,
                new ChartFrameInfo
                {
                    MapFrameName = mapFrame.Name,
                    MapMemberUri = member.URI,
                    ChartName = chartName,
                },
                elementName,
                false,
                null) as ChartFrame
                ?? throw new InvalidOperationException("ArcGIS Pro did not create a chart frame.");
            return (object)new
            {
                layout = layout.Name,
                element = frame.Name,
                frame_type = frame.GetType().Name,
                map_frame = mapFrame.Name,
                map_member = member.Name,
                chart_name = chartName,
                source_data_modified = false,
            };
        });
    }

    private static void EnsureElementNameAvailable(Layout layout, string name)
    {
        if (layout.FindElement(name) is not null)
        {
            throw new InvalidOperationException($"Layout element already exists: {name}");
        }
    }

    private static MapFrame ResolveMapFrame(Layout layout, string? frameName)
    {
        var frames = layout.GetElementsAsFlattenedList().OfType<MapFrame>().ToArray();
        if (!string.IsNullOrWhiteSpace(frameName))
        {
            return frames.FirstOrDefault(frame => string.Equals(
                frame.Name, frameName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Map frame not found: {frameName}");
        }
        return frames.FirstOrDefault()
            ?? throw new InvalidOperationException($"Layout has no map frame: {layout.Name}");
    }

    private static MapMember ResolveMapMember(Map map, string name)
    {
        return map.GetLayersAsFlattenedList().OfType<MapMember>()
            .Concat(map.StandaloneTables.Cast<MapMember>())
            .FirstOrDefault(member => string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Layer or standalone table not found: {name}");
    }

    private static IReadOnlyList<FieldDescription> FieldDescriptions(MapMember member)
    {
        return member switch
        {
            BasicFeatureLayer layer => layer.GetFieldDescriptions(),
            StandaloneTable table => table.GetFieldDescriptions(),
            _ => throw new InvalidOperationException(
                $"Map member does not expose tabular fields: {member.Name}"),
        };
    }

    private static TableFrameStyleItem? ResolveTableFrameStyle(JsonElement payload)
    {
        var itemName = BridgeSupport.String(payload, "style_item_name");
        var search = BridgeSupport.String(payload, "style_search", itemName);
        if (string.IsNullOrWhiteSpace(itemName) && string.IsNullOrWhiteSpace(search))
        {
            return null;
        }
        var styleName = BridgeSupport.String(payload, "style_name");
        var candidates = Project.Current.GetItems<StyleProjectItem>()
            .Where(style => string.IsNullOrWhiteSpace(styleName)
                || string.Equals(style.Name, styleName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(style => StyleHelper.SearchTableFrames(style, search ?? string.Empty))
            .ToArray();
        return !string.IsNullOrWhiteSpace(itemName)
            ? candidates.FirstOrDefault(item => string.Equals(
                item.Name, itemName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Table-frame style not found: {itemName}")
            : candidates.FirstOrDefault()
                ?? throw new InvalidOperationException($"No table-frame style matched: {search}");
    }
}
