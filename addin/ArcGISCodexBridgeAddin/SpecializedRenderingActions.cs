using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class SpecializedRenderingActions
{
    internal static async Task<object> ConfigureAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var operation = (BridgeSupport.String(payload, "operation") ?? "inspect")
            .Trim().ToLowerInvariant();
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = BridgeSupport.ResolveLayer(map, layerName);
            if (operation == "inspect")
                return DescribeLayer(map, layer, payload, false);

            if (operation == "remove")
            {
                if (!BridgeSupport.Bool(payload, "confirm_remove", false))
                    throw new ArgumentException("confirm_remove=true is required to remove a specialized surface renderer");
                if (layer is not TinLayer && layer is not TerrainLayer)
                    throw new InvalidOperationException("Renderer removal is supported only for TIN and terrain layers");
                var target = ParseTarget(BridgeSupport.String(payload, "target", "Surface"));
                if (layer is TinLayer tin)
                    tin.RemoveRenderer(target);
                else
                    ((TerrainLayer)layer).RemoveRenderer(target);
                return new
                {
                    operation,
                    map = map.Name,
                    layer = layer.Name,
                    removed_target = target.ToString(),
                    state = DescribeLayer(map, layer, payload, true),
                    project_modified = true,
                    source_data_modified = false,
                    project_saved = false,
                };
            }

            if (operation != "apply")
                throw new ArgumentException("operation must be inspect, apply, or remove");

            if (layer is PointCloudSceneLayer pointCloud)
            {
                var rendererType = ParsePointCloudType(BridgeSupport.String(payload, "renderer_type", "StretchRenderer"));
                var field = BridgeSupport.String(payload, "field");
                var available = pointCloud.GetAvailablePointCloudRendererFields(rendererType);
                if (rendererType != PointCloudRendererType.RGBRenderer)
                {
                    field ??= available.FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(field))
                        throw new InvalidOperationException($"No field is available for point-cloud renderer {rendererType}");
                    if (!available.Any(item => string.Equals(item, field, StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException($"Point-cloud renderer field is not available: {field}");
                }
                var renderer = pointCloud.CreateRenderer(new PointCloudRendererDefinition(rendererType, field));
                pointCloud.SetRenderer(renderer);
                if (payload.TryGetProperty("eye_dome_lighting", out _))
                    pointCloud.SetEyeDomeLightingEnabled(BridgeSupport.Bool(payload, "eye_dome_lighting", true));
                if (payload.TryGetProperty("eye_dome_radius", out _))
                    pointCloud.SetEyeDomeLightingRadius(Math.Clamp(BridgeSupport.Double(payload, "eye_dome_radius", 1), 0, 100));
                if (payload.TryGetProperty("eye_dome_strength", out _))
                    pointCloud.SetEyeDomeLightingStrength(Math.Clamp(BridgeSupport.Double(payload, "eye_dome_strength", 1), 0, 100));
                return ApplyResult(map, layer, renderer.GetType().Name, null, payload);
            }

            if (layer is not TinLayer && layer is not TerrainLayer)
                throw new InvalidOperationException("Layer must be a TIN, terrain, or point-cloud scene layer");
            var definition = BuildTinDefinition(payload);
            var rendererTarget = payload.TryGetProperty("target", out _)
                ? ParseTarget(BridgeSupport.String(payload, "target"))
                : DefaultTarget(BridgeSupport.String(payload, "renderer_type"));
            CIMTinRenderer rendererResult;
            if (layer is TinLayer tinLayer)
            {
                if (!tinLayer.CanCreateRenderer(definition))
                    throw new InvalidOperationException($"TIN layer cannot create renderer {definition.GetType().Name}");
                rendererResult = tinLayer.CreateRenderer(definition);
                if (!tinLayer.CanSetRenderer(rendererResult, rendererTarget))
                    throw new InvalidOperationException($"TIN layer cannot set renderer on target {rendererTarget}");
                tinLayer.SetRenderer(rendererResult, rendererTarget);
            }
            else
            {
                var terrainLayer = (TerrainLayer)layer;
                if (!terrainLayer.CanCreateRenderer(definition))
                    throw new InvalidOperationException($"Terrain layer cannot create renderer {definition.GetType().Name}");
                rendererResult = terrainLayer.CreateRenderer(definition);
                if (!terrainLayer.CanSetRenderer(rendererResult, rendererTarget))
                    throw new InvalidOperationException($"Terrain layer cannot set renderer on target {rendererTarget}");
                terrainLayer.SetRenderer(rendererResult, rendererTarget);
            }
            return ApplyResult(map, layer, rendererResult.GetType().Name, rendererTarget, payload);
        });
    }

    internal static async Task<object> InspectPrintersAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        return await QueuedTask.Run<object>(() =>
        {
            var requestedLayout = BridgeSupport.String(payload, "layout_name");
            var layouts = Project.Current.GetItems<LayoutProjectItem>()
                .Select(item => item.GetLayout())
                .Where(layout => string.IsNullOrWhiteSpace(requestedLayout)
                    || string.Equals(layout.Name, requestedLayout, StringComparison.OrdinalIgnoreCase))
                .Select(layout =>
                {
                    var page = layout.GetDefinition().Page;
                    return new
                    {
                        name = layout.Name,
                        configured_printer = page.PrinterPreferences?.PrinterName,
                        page_width = page.Width,
                        page_height = page.Height,
                        page_units = page.Units.ToString(),
                    };
                }).ToArray();
            var activeView = MapView.Active;
            return new
            {
                system_printer_enumeration_available = false,
                note = "The public ArcGIS Pro SDK exposes print settings and validation but not a typed installed-printer catalog without taking a System.Drawing.Common dependency.",
                layouts,
                active_map_view = activeView?.Map.Name,
                active_map_view_can_print = activeView?.CanPrint,
                printing_executed = false,
                external_state_modified = false,
                project_modified = false,
                source_data_modified = false,
                project_saved = false,
            };
        });
    }

    private static object ApplyResult(
        Map map,
        Layer layer,
        string rendererType,
        SurfaceRendererTarget? target,
        JsonElement payload) => new
    {
        operation = "apply",
        map = map.Name,
        layer = layer.Name,
        renderer_type = rendererType,
        target = target?.ToString(),
        state = DescribeLayer(map, layer, payload, true),
        project_modified = true,
        source_data_modified = false,
        project_saved = false,
    };

    private static object DescribeLayer(Map map, Layer layer, JsonElement payload, bool modified)
    {
        var includeCim = BridgeSupport.Bool(payload, "include_cim", false);
        object renderers = layer switch
        {
            TinLayer tin => tin.GetRenderersAsDictionary().Select(item => new
            {
                target = item.Key.ToString(),
                type = item.Value.GetType().Name,
                cim_json = includeCim ? BoundedJson(item.Value.ToJson()) : null,
            }).ToArray(),
            TerrainLayer terrain => terrain.GetRenderersAsDictionary().Select(item => new
            {
                target = item.Key.ToString(),
                type = item.Value.GetType().Name,
                cim_json = includeCim ? BoundedJson(item.Value.ToJson()) : null,
            }).ToArray(),
            PointCloudSceneLayer pointCloud => new[]
            {
                new
                {
                    target = "PointCloud",
                    type = pointCloud.GetRenderer()?.GetType().Name,
                    cim_json = includeCim && pointCloud.GetRenderer() is { } renderer
                        ? BoundedJson(renderer.ToJson()) : null,
                },
            },
            _ => Array.Empty<object>(),
        };
        var pointCloudState = layer is PointCloudSceneLayer pc
            ? new
            {
                renderer_type = pc.RendererType.ToString(),
                eye_dome_lighting = pc.IsEyeDomeLightingEnabled,
                eye_dome_radius = Finite(pc.EyeDomeLightingRadius),
                eye_dome_strength = Finite(pc.EyeDomeLightingStrength),
                available_fields = Enum.GetValues<PointCloudRendererType>()
                    .Where(type => type != PointCloudRendererType.UnknownRenderer)
                    .ToDictionary(
                        type => type.ToString(),
                        type => pc.GetAvailablePointCloudRendererFields(type).Take(500).ToArray()),
            }
            : null;
        return new
        {
            map = map.Name,
            layer = layer.Name,
            layer_type = layer.GetType().Name,
            renderers,
            point_cloud = pointCloudState,
            project_modified = modified,
            source_data_modified = false,
            project_saved = false,
        };
    }

    private static TinRendererDefinition BuildTinDefinition(JsonElement payload)
    {
        var type = (BridgeSupport.String(payload, "renderer_type") ?? string.Empty)
            .Trim().ToLowerInvariant();
        var label = BridgeSupport.String(payload, "label", type) ?? type;
        var description = BridgeSupport.String(payload, "description", string.Empty) ?? string.Empty;
        var breaks = Math.Clamp(BridgeSupport.Int(payload, "class_count", 10), 2, 100);
        var classification = BridgeSupport.Classification(BridgeSupport.String(payload, "classification"));
        var colorRamp = BridgeSupport.ColorRamp(payload);
        var polygon = PolygonSymbol(payload).MakeSymbolReference();
        var point = PointSymbol(payload).MakeSymbolReference();
        var line = LineSymbol(payload, false).MakeSymbolReference();
        return type switch
        {
            "face" or "surface" => new TinFaceRendererDefinition(polygon, label, description),
            "edge" or "edges" => new TinEdgeRendererDefintion(line, label, description),
            "node" or "nodes" => new TinNodeRendererDefinition(point, label, description),
            "contour" or "contours" => new TinContourRendererDefinition(
                line, label, description, LineSymbol(payload, true).MakeSymbolReference(),
                BridgeSupport.String(payload, "index_label", "Index Contour")!,
                BridgeSupport.String(payload, "index_description", string.Empty)!)
            {
                ContourInterval = Positive(payload, "contour_interval", 100),
                ReferenceHeight = BridgeSupport.Double(payload, "reference_height", 0),
                ContourFactor = Math.Clamp(BridgeSupport.Int(payload, "index_contour_factor", 5), 1, 100),
            },
            "face_elevation" or "face_class_breaks" => new TinFaceClassBreaksRendererDefinition
            {
                ClassificationMethod = classification,
                BreakCount = breaks,
                SymbolTemplate = polygon,
                ColorRamp = colorRamp,
            },
            "face_aspect" or "aspect" => new TinFaceClassBreaksAspectRendererDefinition(),
            "node_elevation" or "node_class_breaks" => new TinNodeClassBreaksRendererDefinition
            {
                ClassificationMethod = classification,
                BreakCount = breaks,
                SymbolTemplate = point,
                ColorRamp = colorRamp,
            },
            "terrain_points" or "terrain_point_elevation" => new TerrainPointClassBreaksRendererDefinition
            {
                ClassificationMethod = classification,
                BreakCount = breaks,
                SymbolTemplate = point,
                ColorRamp = colorRamp,
            },
            "dirty_area" => new TerrainDirtyAreaRendererDefinition(polygon, label, description),
            "breakline" or "breaklines" => new TinBreaklineRendererDefinition(line, true),
            _ => throw new ArgumentException(
                "renderer_type must be face, edge, node, contour, face_elevation, face_aspect, node_elevation, terrain_points, dirty_area, or breakline"),
        };
    }

    private static SurfaceRendererTarget DefaultTarget(string? rendererType)
    {
        return (rendererType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "edge" or "edges" or "breakline" or "breaklines" => SurfaceRendererTarget.Edges,
            "node" or "nodes" or "node_elevation" or "node_class_breaks"
                or "terrain_points" or "terrain_point_elevation" => SurfaceRendererTarget.Points,
            "contour" or "contours" => SurfaceRendererTarget.Contours,
            "dirty_area" => SurfaceRendererTarget.DirtyArea,
            _ => SurfaceRendererTarget.Surface,
        };
    }

    private static SurfaceRendererTarget ParseTarget(string? value)
    {
        if (Enum.TryParse<SurfaceRendererTarget>(value, true, out var target))
            return target;
        throw new ArgumentException($"Unsupported surface renderer target: {value}");
    }

    private static PointCloudRendererType ParsePointCloudType(string? value)
    {
        if (Enum.TryParse<PointCloudRendererType>(value, true, out var type)
            && type != PointCloudRendererType.UnknownRenderer)
            return type;
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "stretch" => PointCloudRendererType.StretchRenderer,
            "class_breaks" or "classified" => PointCloudRendererType.ClassBreaksRenderer,
            "unique_value" or "unique" => PointCloudRendererType.UniqueValueRenderer,
            "rgb" => PointCloudRendererType.RGBRenderer,
            _ => throw new ArgumentException($"Unsupported point-cloud renderer type: {value}"),
        };
    }

    private static CIMPolygonSymbol PolygonSymbol(JsonElement payload) => SymbolFactory.Instance
        .ConstructPolygonSymbol(
            BridgeSupport.Color(payload, "color", ColorFactory.Instance.CreateRGBColor(175, 190, 150, 80)),
            SimpleFillStyle.Solid,
            SymbolFactory.Instance.ConstructStroke(
                BridgeSupport.Color(payload, "outline_color", ColorFactory.Instance.CreateRGBColor(65, 65, 65)),
                Math.Clamp(BridgeSupport.Double(payload, "width", 0.8), 0, 100),
                SimpleLineStyle.Solid));

    private static CIMPointSymbol PointSymbol(JsonElement payload) => SymbolFactory.Instance
        .ConstructPointSymbol(
            BridgeSupport.Color(payload, "color", ColorFactory.Instance.CreateRGBColor(70, 115, 165)),
            Math.Clamp(BridgeSupport.Double(payload, "size", 4), 0.1, 1000),
            SimpleMarkerStyle.Circle);

    private static CIMLineSymbol LineSymbol(JsonElement payload, bool index) => SymbolFactory.Instance
        .ConstructLineSymbol(
            BridgeSupport.Color(payload, index ? "index_color" : "color",
                index ? ColorFactory.Instance.CreateRGBColor(60, 60, 60)
                    : ColorFactory.Instance.CreateRGBColor(110, 95, 75)),
            Math.Clamp(BridgeSupport.Double(payload, index ? "index_width" : "width", index ? 1.5 : 0.7), 0, 100),
            SimpleLineStyle.Solid);

    private static double Positive(JsonElement payload, string name, double fallback)
    {
        var value = BridgeSupport.Double(payload, name, fallback);
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentException($"{name} must be finite and greater than zero");
        return value;
    }

    private static string BoundedJson(string value) => value.Length <= 2 * 1024 * 1024
        ? value : throw new InvalidOperationException("Renderer CIM JSON exceeds 2 MiB response limit");

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
}
