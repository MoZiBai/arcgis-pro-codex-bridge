using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class GraphicsActions
{
    internal static async Task<object> DrawAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var graphicsLayerName = BridgeSupport.String(payload, "graphics_layer_name", "Codex Graphics")!;

        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var graphicsLayer = map.GetLayersAsFlattenedList()
                .OfType<GraphicsLayer>()
                .FirstOrDefault(layer => string.Equals(
                    layer.Name, graphicsLayerName, StringComparison.OrdinalIgnoreCase));
            graphicsLayer ??= LayerFactory.Instance.CreateLayer<GraphicsLayer>(
                new GraphicsLayerCreationParams { Name = graphicsLayerName }, map);

            if (BridgeSupport.Bool(payload, "clear_existing", false))
            {
                graphicsLayer.RemoveElements(graphicsLayer.GetElementsAsFlattenedList());
            }

            if (!payload.TryGetProperty("graphics", out var graphics)
                || graphics.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException("graphics array is required");
            }

            var created = new List<string>();
            var index = 0;
            foreach (var item in graphics.EnumerateArray())
            {
                index++;
                var type = BridgeSupport.String(item, "type")?.ToLowerInvariant()
                    ?? throw new ArgumentException("Each graphic requires a type");
                var name = BridgeSupport.String(item, "name", $"Codex Graphic {index}")!;
                var geometry = Geometry(item, type, map.SpatialReference);

                if (type == "text")
                {
                    var textConfig = item.TryGetProperty("text_symbol", out var textSymbol)
                        && textSymbol.ValueKind == JsonValueKind.Object
                        ? textSymbol
                        : default;
                    ElementFactory.Instance.CreateTextGraphicElement(
                        graphicsLayer,
                        TextType.PointText,
                        geometry,
                        BridgeSupport.TextSymbol(textConfig),
                        BridgeSupport.String(item, "text", string.Empty),
                        name,
                        false,
                        null);
                }
                else
                {
                    var symbolConfig = item.TryGetProperty("symbol", out var symbol)
                        && symbol.ValueKind == JsonValueKind.Object
                        ? symbol
                        : default;
                    ElementFactory.Instance.CreateGraphicElement(
                        graphicsLayer,
                        geometry,
                        Symbol(type, symbolConfig),
                        name,
                        false,
                        null);
                }
                created.Add(name);
            }

            return (object)new
            {
                map = map.Name,
                graphics_layer = graphicsLayer.Name,
                created,
            };
        });
    }

    internal static async Task<object> ListAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var requestedLayer = BridgeSupport.String(payload, "graphics_layer_name");
        var includeCim = BridgeSupport.Bool(payload, "include_cim", false);
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layers = map.GetLayersAsFlattenedList().OfType<GraphicsLayer>()
                .Where(layer => string.IsNullOrWhiteSpace(requestedLayer)
                    || string.Equals(layer.Name, requestedLayer, StringComparison.OrdinalIgnoreCase))
                .Select(layer => new
                {
                    name = layer.Name,
                    uri = layer.URI,
                    visible = layer.IsVisible,
                    selectable = layer.IsSelectable,
                    snappable = layer.IsSnappable,
                    reference_scale = layer.ReferenceScale,
                    legend_status = layer.LegendStatus.ToString(),
                    elements = layer.GetElementsAsFlattenedList().Select(element =>
                        DescribeMapGraphic(element, includeCim)).ToArray(),
                }).ToArray();
            return (object)new
            {
                map = map.Name,
                graphics_layers = layers,
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> ManageAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var layerName = BridgeSupport.String(payload, "graphics_layer_name", "Codex Graphics")!;
        var operation = BridgeSupport.String(payload, "operation")?.ToLowerInvariant()
            ?? throw new ArgumentException("operation is required");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = ResolveGraphicsLayer(map, layerName);
            var container = (IElementContainer)layer;
            var affected = new List<string>();
            if (operation == "update")
            {
                var elementName = Required(payload, "element_name");
                var element = ResolveElement(layer, elementName);
                if (payload.TryGetProperty("new_name", out _))
                    element.SetName(Required(payload, "new_name"));
                if (payload.TryGetProperty("visible", out _))
                    element.SetVisible(BridgeSupport.Bool(payload, "visible"));
                if (payload.TryGetProperty("locked", out _))
                    element.SetLocked(BridgeSupport.Bool(payload, "locked"));
                if (payload.TryGetProperty("locked_aspect_ratio", out _))
                    element.SetLockedAspectRatio(BridgeSupport.Bool(payload, "locked_aspect_ratio"));
                if (payload.TryGetProperty("x", out _))
                    element.SetX(BridgeSupport.Double(payload, "x"));
                if (payload.TryGetProperty("y", out _))
                    element.SetY(BridgeSupport.Double(payload, "y"));
                if (payload.TryGetProperty("width", out _))
                    element.SetWidth(Math.Max(0, BridgeSupport.Double(payload, "width")));
                if (payload.TryGetProperty("height", out _))
                    element.SetHeight(Math.Max(0, BridgeSupport.Double(payload, "height")));
                if (payload.TryGetProperty("rotation", out _))
                    element.SetRotation(BridgeSupport.Double(payload, "rotation"));
                if (payload.TryGetProperty("anchor", out _))
                {
                    var raw = BridgeSupport.String(payload, "anchor");
                    if (!Enum.TryParse<Anchor>(raw, true, out var anchor))
                        throw new ArgumentException($"Unsupported anchor: {raw}");
                    element.SetAnchor(anchor);
                }
                affected.Add(element.Name);
            }
            else if (operation == "remove")
            {
                if (!BridgeSupport.Bool(payload, "confirm_remove"))
                    throw new ArgumentException("confirm_remove=true is required");
                foreach (var name in ElementNames(payload))
                {
                    ResolveElement(layer, name);
                    container.RemoveElement(name, true);
                    affected.Add(name);
                }
            }
            else if (operation == "clear")
            {
                if (!BridgeSupport.Bool(payload, "confirm_clear"))
                    throw new ArgumentException("confirm_clear=true is required");
                affected.AddRange(layer.GetElementsAsFlattenedList().Select(item => item.Name));
                container.ClearElements();
            }
            else if (operation == "move")
            {
                var elementName = Required(payload, "element_name");
                ResolveElement(layer, elementName);
                container.MoveElement(elementName, Math.Max(0, BridgeSupport.Int(payload, "index")));
                affected.Add(elementName);
            }
            else if (operation is "select" or "unselect")
            {
                var names = ElementNames(payload);
                foreach (var name in names)
                    ResolveElement(layer, name);
                if (operation == "select")
                    container.SelectElements(names, true, BridgeSupport.Bool(payload, "clear_selection", false));
                else
                    container.UnselectElements(names, true, false);
                affected.AddRange(names);
            }
            else
            {
                throw new ArgumentException(
                    "operation must be update, remove, clear, move, select, or unselect");
            }
            return (object)new
            {
                map = map.Name,
                graphics_layer = layer.Name,
                operation,
                affected,
                element_count = layer.GetElementsAsFlattenedList().Count,
                selected = container.GetSelectedElements().Select(item => item.Name).ToArray(),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> GetCimAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var layerName = BridgeSupport.String(payload, "graphics_layer_name", "Codex Graphics")!;
        var elementName = Required(payload, "element_name");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = ResolveGraphicsLayer(map, layerName);
            return DescribeMapGraphic(ResolveElement(layer, elementName), true);
        });
    }

    internal static async Task<object> SetCimAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!BridgeSupport.Bool(payload, "confirm_replace_definition"))
            throw new ArgumentException("confirm_replace_definition=true is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var layerName = BridgeSupport.String(payload, "graphics_layer_name", "Codex Graphics")!;
        var elementName = Required(payload, "element_name");
        var cimJson = Required(payload, "cim_json");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = ResolveGraphicsLayer(map, layerName);
            var element = ResolveElement(layer, elementName);
            var current = element.GetDefinition();
            var replacement = CimObjectActions.ParseSameType(current, cimJson);
            CimObjectActions.PreserveElementLinks(current, replacement, payload);
            element.SetDefinition(replacement);
            return DescribeMapGraphic(element, true);
        });
    }

    private static object DescribeMapGraphic(IElement element, bool includeCim)
    {
        var definition = element.GetDefinition();
        var bounds = element.GetBounds(true);
        return new
        {
            name = element.Name,
            element_type = element.GetType().Name,
            cim_type = definition.GetType().Name,
            visible = element.IsVisible,
            locked = element.IsLocked,
            // IsExpandedInTOC is a GUI-thread-only property while map graphic
            // geometry/CIM inspection must run on the MCT. Avoid a cross-thread
            // access that can fail the entire inventory response.
            expanded = (bool?)null,
            expanded_state_available = false,
            z_order = element.ZOrder,
            x = element.GetX(),
            y = element.GetY(),
            width = element.GetWidth(),
            height = element.GetHeight(),
            rotation = element.GetRotation(),
            anchor = element.GetAnchor().ToString(),
            locked_aspect_ratio = element.GetLockedAspectRatio(),
            bounds = new
            {
                xmin = bounds.XMin,
                ymin = bounds.YMin,
                xmax = bounds.XMax,
                ymax = bounds.YMax,
                spatial_reference = bounds.SpatialReference?.Name,
            },
            cim_json = includeCim ? definition.ToJson() : null,
            source_data_modified = false,
        };
    }

    private static GraphicsLayer ResolveGraphicsLayer(Map map, string layerName)
    {
        return map.GetLayersAsFlattenedList().OfType<GraphicsLayer>().FirstOrDefault(layer => string.Equals(
            layer.Name, layerName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Graphics layer not found: {layerName}");
    }

    private static IElement ResolveElement(GraphicsLayer layer, string elementName)
    {
        return ((IElementContainer)layer).FindElement(elementName, true)
            ?? throw new InvalidOperationException($"Map graphic not found: {elementName}");
    }

    private static string[] ElementNames(JsonElement payload)
    {
        var names = BridgeSupport.Strings(payload, "element_names");
        if (names.Length == 0 && !string.IsNullOrWhiteSpace(BridgeSupport.String(payload, "element_name")))
            names = new[] { Required(payload, "element_name") };
        if (names.Length == 0)
            throw new ArgumentException("element_name or element_names is required");
        return names;
    }

    private static string Required(JsonElement payload, string name)
    {
        return BridgeSupport.String(payload, name)
            ?? throw new ArgumentException($"{name} is required");
    }

    private static Geometry Geometry(JsonElement item, string type, SpatialReference spatialReference)
    {
        if (type is "point" or "text")
        {
            var coordinates = Coordinates(item);
            if (coordinates.Count < 1)
            {
                throw new ArgumentException("Point/text graphic requires coordinates: [x, y]");
            }
            return MapPointBuilderEx.CreateMapPoint(
                coordinates[0].X, coordinates[0].Y, spatialReference);
        }
        if (type is "polyline" or "line")
        {
            return PolylineBuilderEx.CreatePolyline(Coordinates(item), spatialReference);
        }
        if (type is "polygon")
        {
            return PolygonBuilderEx.CreatePolygon(Coordinates(item), spatialReference);
        }
        if (type is "rectangle" or "envelope")
        {
            if (!item.TryGetProperty("bounds", out var bounds)
                || bounds.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Rectangle graphic requires bounds");
            }
            return EnvelopeBuilderEx.CreateEnvelope(
                BridgeSupport.Double(bounds, "xmin"),
                BridgeSupport.Double(bounds, "ymin"),
                BridgeSupport.Double(bounds, "xmax"),
                BridgeSupport.Double(bounds, "ymax"),
                spatialReference);
        }
        throw new ArgumentException($"Unsupported graphic type: {type}");
    }

    private static List<Coordinate2D> Coordinates(JsonElement item)
    {
        if (!item.TryGetProperty("coordinates", out var coordinates)
            || coordinates.ValueKind != JsonValueKind.Array)
        {
            return new List<Coordinate2D>();
        }

        var raw = coordinates.EnumerateArray().ToArray();
        if (raw.Length >= 2 && raw[0].ValueKind == JsonValueKind.Number)
        {
            return new List<Coordinate2D>
            {
                new(raw[0].GetDouble(), raw[1].GetDouble()),
            };
        }

        return raw
            .Where(item => item.ValueKind == JsonValueKind.Array)
            .Select(item => item.EnumerateArray().ToArray())
            .Where(pair => pair.Length >= 2)
            .Select(pair => new Coordinate2D(pair[0].GetDouble(), pair[1].GetDouble()))
            .ToList();
    }

    private static CIMSymbol Symbol(string type, JsonElement config)
    {
        var color = BridgeSupport.Color(
            config, "color", ColorFactory.Instance.CreateRGBColor(45, 105, 160, 80));
        var outline = BridgeSupport.Color(
            config, "outline_color", ColorFactory.Instance.CreateRGBColor(35, 35, 35));
        var width = BridgeSupport.Double(config, "width", 1.2);

        if (type == "point")
        {
            return SymbolFactory.Instance.ConstructPointSymbol(
                color,
                BridgeSupport.Double(config, "size", 7),
                Enum.TryParse<SimpleMarkerStyle>(BridgeSupport.String(config, "marker", "Circle"), true, out var marker)
                    ? marker
                    : SimpleMarkerStyle.Circle);
        }
        if (type is "polyline" or "line")
        {
            return SymbolFactory.Instance.ConstructLineSymbol(
                color,
                width,
                Enum.TryParse<SimpleLineStyle>(BridgeSupport.String(config, "line_style", "Solid"), true, out var line)
                    ? line
                    : SimpleLineStyle.Solid);
        }
        return SymbolFactory.Instance.ConstructPolygonSymbol(
            color,
            SimpleFillStyle.Solid,
            SymbolFactory.Instance.ConstructStroke(outline, width, SimpleLineStyle.Solid));
    }
}
