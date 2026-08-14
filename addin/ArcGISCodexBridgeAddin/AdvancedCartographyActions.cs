using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class AdvancedCartographyActions
{
    internal static async Task<object> ListExtentIndicatorsAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var includeCim = BridgeSupport.Bool(payload, "include_cim", false);
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var target = ResolveMapFrame(layout, BridgeSupport.String(payload, "map_frame_name"));
            var frames = layout.GetElementsAsFlattenedList().OfType<MapFrame>().ToArray();
            var definition = (CIMMapFrame)target.GetDefinition();
            var indicators = definition.ExtentIndicators ?? Array.Empty<CIMExtentIndicator>();
            return (object)new
            {
                layout = layout.Name,
                map_frame = target.Name,
                extent_indicators = indicators.Select((indicator, index) => new
                {
                    index,
                    name = indicator.Name,
                    source_map_frame = frames.FirstOrDefault(frame => string.Equals(
                        frame.Name, indicator.SourceMapFrame, StringComparison.OrdinalIgnoreCase))?.Name,
                    source_map_frame_reference = indicator.SourceMapFrame,
                    visible = indicator.IsVisible,
                    indicator_type = indicator.ExtentIndicatorType.ToString(),
                    symbolize_exterior = indicator.SymbolizeExterior,
                    avoid_label_conflict = indicator.AvoidLabelConflict,
                    collapse_size = indicator.CollapseSize,
                    leader_type = indicator.LeaderType.ToString(),
                    has_frame_symbol = indicator.Symbol is not null,
                    has_point_symbol = indicator.PointSymbol is not null,
                    has_leader_symbol = indicator.LeaderSymbol is not null,
                    cim_json = includeCim ? indicator.ToJson() : null,
                }).ToArray(),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> AddExtentIndicatorAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var sourceName = BridgeSupport.String(payload, "source_map_frame_name")
            ?? throw new ArgumentException("source_map_frame_name is required");
        var indicatorName = BridgeSupport.String(payload, "indicator_name", "范围指示器")!;
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var target = ResolveMapFrame(layout, BridgeSupport.String(payload, "map_frame_name"));
            var source = ResolveMapFrame(layout, sourceName);
            if (ReferenceEquals(target, source) || string.Equals(
                target.Name, source.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The source and target map frames must be different.");
            }

            var definition = (CIMMapFrame)target.GetDefinition();
            var indicators = (definition.ExtentIndicators ?? Array.Empty<CIMExtentIndicator>()).ToList();
            if (indicators.Any(item => string.Equals(item.Name, indicatorName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Extent indicator already exists: {indicatorName}");
            }
            var indicator = new CIMExtentIndicator
            {
                Name = indicatorName,
                SourceMapFrame = source.Name,
                IsVisible = BridgeSupport.Bool(payload, "visible", true),
                ExtentIndicatorType = ParseExtentIndicatorType(
                    BridgeSupport.String(payload, "indicator_type", "Frame")),
                SymbolizeExterior = BridgeSupport.Bool(payload, "symbolize_exterior", false),
                AvoidLabelConflict = BridgeSupport.Bool(payload, "avoid_label_conflict", false),
                CollapseSize = Math.Max(0, BridgeSupport.Double(payload, "collapse_size", 0)),
                LeaderType = ParseLeaderType(BridgeSupport.String(payload, "leader_type", "None")),
                Symbol = ExtentPolygonSymbol(payload),
                PointSymbol = ExtentPointSymbol(payload),
                LeaderSymbol = ExtentLeaderSymbol(payload),
            };
            indicators.Add(indicator);
            definition.ExtentIndicators = indicators.ToArray();
            definition.ExtentIndicatorsExpanded = true;
            target.SetDefinition(definition);
            return (object)new
            {
                layout = layout.Name,
                map_frame = target.Name,
                indicator = indicator.Name,
                source_map_frame = source.Name,
                source_map_frame_reference = source.Name,
                indicator_type = indicator.ExtentIndicatorType.ToString(),
                leader_type = indicator.LeaderType.ToString(),
                indicator_count = indicators.Count,
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> ConfigureExtentIndicatorAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var indicatorName = BridgeSupport.String(payload, "indicator_name")
            ?? throw new ArgumentException("indicator_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var target = ResolveMapFrame(layout, BridgeSupport.String(payload, "map_frame_name"));
            var definition = (CIMMapFrame)target.GetDefinition();
            var indicators = definition.ExtentIndicators?.ToArray() ?? Array.Empty<CIMExtentIndicator>();
            var index = Array.FindIndex(indicators, item => string.Equals(
                item.Name, indicatorName, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new InvalidOperationException($"Extent indicator not found: {indicatorName}");
            }
            var indicator = indicators[index].Clone() as CIMExtentIndicator ?? indicators[index];
            var newName = BridgeSupport.String(payload, "new_name");
            if (!string.IsNullOrWhiteSpace(newName))
            {
                if (indicators.Where((_, candidateIndex) => candidateIndex != index).Any(item => string.Equals(
                    item.Name, newName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"Extent indicator already exists: {newName}");
                }
                indicator.Name = newName;
            }
            var sourceName = BridgeSupport.String(payload, "source_map_frame_name");
            if (!string.IsNullOrWhiteSpace(sourceName))
            {
                var source = ResolveMapFrame(layout, sourceName);
                if (string.Equals(target.Name, source.Name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("The source and target map frames must be different.");
                }
                indicator.SourceMapFrame = source.Name;
            }
            if (payload.TryGetProperty("visible", out _))
                indicator.IsVisible = BridgeSupport.Bool(payload, "visible", true);
            if (payload.TryGetProperty("indicator_type", out _))
                indicator.ExtentIndicatorType = ParseExtentIndicatorType(
                    BridgeSupport.String(payload, "indicator_type"));
            if (payload.TryGetProperty("symbolize_exterior", out _))
                indicator.SymbolizeExterior = BridgeSupport.Bool(payload, "symbolize_exterior", false);
            if (payload.TryGetProperty("avoid_label_conflict", out _))
                indicator.AvoidLabelConflict = BridgeSupport.Bool(payload, "avoid_label_conflict", false);
            if (payload.TryGetProperty("collapse_size", out _))
                indicator.CollapseSize = Math.Max(0, BridgeSupport.Double(payload, "collapse_size"));
            if (payload.TryGetProperty("leader_type", out _))
                indicator.LeaderType = ParseLeaderType(BridgeSupport.String(payload, "leader_type"));
            if (HasAny(payload, "frame_color", "fill_color", "frame_width"))
                indicator.Symbol = ExtentPolygonSymbol(payload);
            if (HasAny(payload, "point_color", "point_size"))
                indicator.PointSymbol = ExtentPointSymbol(payload);
            if (HasAny(payload, "leader_color", "leader_width"))
                indicator.LeaderSymbol = ExtentLeaderSymbol(payload);

            indicators[index] = indicator;
            definition.ExtentIndicators = indicators;
            target.SetDefinition(definition);
            return (object)new
            {
                layout = layout.Name,
                map_frame = target.Name,
                old_name = indicatorName,
                indicator = indicator.Name,
                source_map_frame_reference = indicator.SourceMapFrame,
                visible = indicator.IsVisible,
                indicator_type = indicator.ExtentIndicatorType.ToString(),
                symbolize_exterior = indicator.SymbolizeExterior,
                avoid_label_conflict = indicator.AvoidLabelConflict,
                collapse_size = indicator.CollapseSize,
                leader_type = indicator.LeaderType.ToString(),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> RemoveExtentIndicatorsAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var names = BridgeSupport.Strings(payload, "indicator_names");
        if (names.Length == 0)
        {
            throw new ArgumentException("indicator_names must contain at least one name");
        }
        if (!BridgeSupport.Bool(payload, "confirm_remove", false))
        {
            throw new ArgumentException("confirm_remove=true is required to remove extent indicators");
        }
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var target = ResolveMapFrame(layout, BridgeSupport.String(payload, "map_frame_name"));
            var definition = (CIMMapFrame)target.GetDefinition();
            var indicators = definition.ExtentIndicators ?? Array.Empty<CIMExtentIndicator>();
            var remaining = indicators.Where(item => !names.Contains(
                item.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
            definition.ExtentIndicators = remaining;
            target.SetDefinition(definition);
            return (object)new
            {
                layout = layout.Name,
                map_frame = target.Name,
                requested = names,
                removed = indicators.Length - remaining.Length,
                remaining = remaining.Select(item => item.Name).ToArray(),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> ConfigureMapClippingAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(BridgeSupport.String(payload, "map_name"));
            var definition = map.GetDefinition();
            var mode = ParseClippingMode(BridgeSupport.String(payload, "mode", "None"));
            definition.ClippingMode = mode;

            string? clippingLayerName = null;
            if (mode == ClippingMode.CustomShape)
            {
                clippingLayerName = BridgeSupport.String(payload, "clipping_layer_name")
                    ?? throw new ArgumentException("clipping_layer_name is required for CustomShape clipping");
                definition.CustomClippingShapeURI = BridgeSupport.ResolveLayer(map, clippingLayerName).URI;
            }
            else if (payload.TryGetProperty("clear_custom_shape", out _)
                ? BridgeSupport.Bool(payload, "clear_custom_shape", true)
                : mode == ClippingMode.None)
            {
                definition.CustomClippingShapeURI = null;
            }

            var excludedNames = BridgeSupport.Strings(payload, "excluded_layer_names");
            if (payload.TryGetProperty("excluded_layer_names", out _))
            {
                definition.LayersExcludedFromClipping = excludedNames
                    .Select(name => BridgeSupport.ResolveLayer(map, name).URI)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            if (payload.TryGetProperty("clip_to_full_extent", out _))
                definition.ClipToFullExtent = BridgeSupport.Bool(payload, "clip_to_full_extent", false);
            if (payload.TryGetProperty("border_visible", out _)
                && !BridgeSupport.Bool(payload, "border_visible", true))
            {
                definition.ClippingAreaBorderSymbol = null;
            }
            else if (BridgeSupport.Bool(payload, "border_visible", definition.ClippingAreaBorderSymbol is not null)
                || HasAny(payload, "border_color", "border_width"))
            {
                definition.ClippingAreaBorderSymbol = SymbolFactory.Instance.ConstructLineSymbol(
                    BridgeSupport.Color(payload, "border_color", ColorFactory.Instance.CreateRGBColor(45, 45, 45)),
                    Math.Max(0, BridgeSupport.Double(payload, "border_width", 0.8)),
                    SimpleLineStyle.Solid).MakeSymbolReference();
            }

            if (mode == ClippingMode.None && BridgeSupport.Bool(payload, "clear_exclusions", true))
            {
                definition.LayersExcludedFromClipping = Array.Empty<string>();
            }
            map.SetDefinition(definition);
            var excluded = map.GetLayersAsFlattenedList().Where(layer =>
                (definition.LayersExcludedFromClipping ?? Array.Empty<string>()).Contains(
                    layer.URI, StringComparer.OrdinalIgnoreCase)).Select(layer => layer.Name).ToArray();
            return (object)new
            {
                map = map.Name,
                clipping_mode = definition.ClippingMode.ToString(),
                clipping_layer = clippingLayerName,
                custom_clipping_shape_uri = definition.CustomClippingShapeURI,
                excluded_layers = excluded,
                clip_to_full_extent = definition.ClipToFullExtent,
                border_visible = definition.ClippingAreaBorderSymbol is not null,
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> AddDynamicTextAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var elementName = BridgeSupport.String(payload, "element_name", "动态文本")!;
        var text = BridgeSupport.String(payload, "text")
            ?? throw new ArgumentException("text is required and may contain native ArcGIS <dyn .../> tags");
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            if (layout.FindElement(elementName) is not null)
            {
                throw new InvalidOperationException($"Layout element already exists: {elementName}");
            }
            var symbol = BridgeSupport.TextSymbol(
                payload.TryGetProperty("text_symbol", out var textSymbol)
                    && textSymbol.ValueKind == JsonValueKind.Object
                        ? textSymbol
                        : default);
            var element = ElementFactory.Instance.CreateTextGraphicElement(
                layout,
                TextType.PointText,
                MapPointBuilderEx.CreateMapPoint(
                    BridgeSupport.Double(payload, "x", 1),
                    BridgeSupport.Double(payload, "y", 1)),
                symbol,
                text,
                elementName,
                false,
                null);
            if (payload.TryGetProperty("rotation", out _))
                element.SetRotation(BridgeSupport.Double(payload, "rotation"));
            if (payload.TryGetProperty("visible", out _))
                element.SetVisible(BridgeSupport.Bool(payload, "visible", true));
            if (payload.TryGetProperty("locked", out _))
                element.SetLocked(BridgeSupport.Bool(payload, "locked", false));
            return (object)new
            {
                layout = layout.Name,
                element = element.Name,
                text,
                contains_dynamic_text = text.Contains("<dyn", StringComparison.OrdinalIgnoreCase),
                x = element.GetX(),
                y = element.GetY(),
                rotation = element.GetRotation(),
                visible = element.IsVisible,
                locked = element.IsLocked,
                source_data_modified = false,
            };
        });
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

    private static ExtentIndicatorType ParseExtentIndicatorType(string? value)
    {
        return Enum.TryParse<ExtentIndicatorType>(value, true, out var parsed)
            ? parsed
            : throw new ArgumentException($"Unsupported extent indicator type: {value}");
    }

    private static LeaderType ParseLeaderType(string? value)
    {
        return Enum.TryParse<LeaderType>(value, true, out var parsed)
            ? parsed
            : throw new ArgumentException($"Unsupported leader type: {value}");
    }

    private static ClippingMode ParseClippingMode(string? value)
    {
        var normalized = (value ?? string.Empty).Replace("_", string.Empty);
        return Enum.TryParse<ClippingMode>(normalized, true, out var parsed)
            ? parsed
            : throw new ArgumentException($"Unsupported clipping mode: {value}");
    }

    private static CIMSymbolReference ExtentPolygonSymbol(JsonElement payload)
    {
        return SymbolFactory.Instance.ConstructPolygonSymbol(
            BridgeSupport.Color(payload, "fill_color", ColorFactory.Instance.CreateRGBColor(255, 255, 255, 0)),
            SimpleFillStyle.Solid,
            SymbolFactory.Instance.ConstructStroke(
                BridgeSupport.Color(payload, "frame_color", ColorFactory.Instance.CreateRGBColor(200, 45, 45)),
                Math.Max(0, BridgeSupport.Double(payload, "frame_width", 1.5)),
                SimpleLineStyle.Solid)).MakeSymbolReference();
    }

    private static CIMSymbolReference ExtentPointSymbol(JsonElement payload)
    {
        return SymbolFactory.Instance.ConstructPointSymbol(
            BridgeSupport.Color(payload, "point_color", ColorFactory.Instance.CreateRGBColor(200, 45, 45)),
            Math.Max(0.1, BridgeSupport.Double(payload, "point_size", 6)),
            SimpleMarkerStyle.Circle).MakeSymbolReference();
    }

    private static CIMSymbolReference ExtentLeaderSymbol(JsonElement payload)
    {
        return SymbolFactory.Instance.ConstructLineSymbol(
            BridgeSupport.Color(payload, "leader_color", ColorFactory.Instance.CreateRGBColor(200, 45, 45)),
            Math.Max(0, BridgeSupport.Double(payload, "leader_width", 1)),
            SimpleLineStyle.Solid).MakeSymbolReference();
    }

    private static bool HasAny(JsonElement payload, params string[] names)
    {
        return payload.ValueKind == JsonValueKind.Object
            && names.Any(name => payload.TryGetProperty(name, out _));
    }
}
