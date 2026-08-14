using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using ArcGIS.Core.Geometry;

namespace ArcGISCodexBridgeAddin;

internal static class ViewInteractionActions
{
    internal static async Task<object> GetContextAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(() => DescribeContext(ResolveActiveView(requestedMap)));
    }

    internal static async Task<object> ConfigureContextAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        MapView? configuredView = null;
        MapTOCContentType? requestedTocContent = null;
        await QueuedTask.Run(() =>
        {
            var view = ResolveActiveView(requestedMap);
            configuredView = view;
            if (payload.TryGetProperty("drawing_paused", out _))
                view.DrawingPaused = BridgeSupport.Bool(payload, "drawing_paused");
            if (payload.TryGetProperty("toc_content", out _))
            {
                var raw = BridgeSupport.String(payload, "toc_content");
                if (!Enum.TryParse<MapTOCContentType>(raw, true, out var content))
                    throw new ArgumentException($"Unsupported toc_content: {raw}");
                if (!view.CanSetMapTOCContent(content))
                    throw new InvalidOperationException($"The active view cannot show TOC content {content}");
                requestedTocContent = content;
            }
            if (payload.TryGetProperty("selected_layer_names", out _))
            {
                var names = BridgeSupport.Strings(payload, "selected_layer_names");
                view.SelectLayers(names.Select(name => ResolveLayer(view.Map, name)).ToArray());
            }
            if (payload.TryGetProperty("selected_table_names", out _))
            {
                var names = BridgeSupport.Strings(payload, "selected_table_names");
                view.SelectStandaloneTables(names.Select(name => ResolveTable(view.Map, name)).ToArray());
            }
            if (payload.TryGetProperty("selected_elevation_surface_names", out _))
            {
                var names = BridgeSupport.Strings(payload, "selected_elevation_surface_names");
                var surfaces = names.Select(name => view.Map.GetElevationSurfaceLayers().FirstOrDefault(item =>
                    string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Elevation surface not found: {name}")).ToArray();
                view.SelectElevationSurfaceLayers(surfaces);
            }
            if (payload.TryGetProperty("selected_elevation_source_layer_names", out _))
            {
                var names = BridgeSupport.Strings(payload, "selected_elevation_source_layer_names");
                view.SelectElevationSourceLayers(names.Select(name => ResolveLayer(view.Map, name)).ToArray());
            }
            if (BridgeSupport.Bool(payload, "clear_toc_selection"))
                view.ClearTOCSelection();
            if (BridgeSupport.Bool(payload, "cancel_drawing"))
                view.CancelDrawing();
            if (payload.TryGetProperty("redraw", out _))
                view.Redraw(BridgeSupport.Bool(payload, "redraw"));
        });
        var resolvedView = configuredView
            ?? throw new InvalidOperationException("The active map view became unavailable");
        if (requestedTocContent.HasValue)
        {
            await OnUiAsync(
                () => resolvedView.SetMapTOCContentAsync(requestedTocContent.Value),
                cancellationToken);
        }
        return await QueuedTask.Run<object>(() => DescribeContext(resolvedView));
    }

    internal static async Task<object> NavigateAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var operation = Required(payload, "operation").ToLowerInvariant();
        var durationValue = BridgeSupport.Int(payload, "duration_ms", 0);
        TimeSpan? duration = durationValue <= 0 ? null : TimeSpan.FromMilliseconds(
            Math.Clamp(durationValue, 1, 60000));
        return await QueuedTask.Run<object>(() =>
        {
            var view = ResolveActiveView(requestedMap);
            var succeeded = operation switch
            {
                "zoom_in_fixed" => view.ZoomInFixed(duration),
                "zoom_out_fixed" => view.ZoomOutFixed(duration),
                "next_camera" => view.NextCamera(duration),
                "previous_camera" => view.PreviousCamera(duration),
                "zoom_selected" => view.ZoomToSelected(
                    duration, BridgeSupport.Bool(payload, "maintain_view_direction", false)),
                "pan_selected" => view.PanToSelected(duration),
                _ => throw new ArgumentException(
                    "operation must be zoom_in_fixed, zoom_out_fixed, next_camera, previous_camera, zoom_selected, or pan_selected"),
            };
            return (object)new
            {
                map = view.Map.Name,
                operation,
                succeeded,
                camera = DescribeCamera(view.Camera),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> FlashFeaturesAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var layerName = Required(payload, "layer_name");
        var objectIds = Longs(payload, "object_ids", 1000);
        if (objectIds.Length == 0)
            throw new ArgumentException("object_ids is required");
        return await QueuedTask.Run<object>(() =>
        {
            var view = ResolveActiveView(requestedMap);
            var layer = ResolveLayer(view.Map, layerName) as BasicFeatureLayer
                ?? throw new InvalidOperationException($"Feature layer not found: {layerName}");
            foreach (var objectId in objectIds)
                view.FlashFeature(layer, objectId, BridgeSupport.Bool(payload, "show_popups", false));
            return (object)new
            {
                map = view.Map.Name,
                layer = layer.Name,
                object_ids = objectIds,
                count = objectIds.Length,
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> ShowPopupAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var memberName = Required(payload, "map_member_name");
        var objectId = RequiredLong(payload, "object_id");
        MapView? resolvedView = null;
        MapMember? resolvedMember = null;
        await QueuedTask.Run(() =>
        {
            var view = ResolveActiveView(requestedMap);
            var member = view.Map.GetMapMembersAsFlattenedList().FirstOrDefault(item => string.Equals(
                item.Name, memberName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Map member not found: {memberName}");
            resolvedView = view;
            resolvedMember = member;
        });
        var activeView = resolvedView
            ?? throw new InvalidOperationException("The active map view became unavailable");
        var mapMember = resolvedMember
            ?? throw new InvalidOperationException($"Map member not found: {memberName}");
        await OnUiAsync(
            () => activeView.ShowPopup(mapMember, objectId, new PopupDefinition
            {
                Append = BridgeSupport.Bool(payload, "append", false),
                Dockable = BridgeSupport.Bool(payload, "dockable", true),
            }),
            cancellationToken);
        return new
        {
            map = activeView.Map.Name,
            map_member = mapMember.Name,
            object_id = objectId,
            popup_shown = true,
            source_data_modified = false,
        };
    }

    internal static async Task<object> ClosePopupsAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var view = await QueuedTask.Run(() => ResolveActiveView(requestedMap));
        await OnUiAsync(view.ClosePopups, cancellationToken);
        return new
        {
            map = view.Map.Name,
            popups_closed = true,
            source_data_modified = false,
        };
    }

    internal static async Task<object> ManageFeatureCacheAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var operation = BridgeSupport.String(payload, "operation", "inspect")!.ToLowerInvariant();
        return await QueuedTask.Run<object>(async () =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            if (payload.TryGetProperty("auto_fill", out _))
                map.SetAutoFillFeatureCache(BridgeSupport.Bool(payload, "auto_fill"));
            var names = BridgeSupport.Strings(payload, "layer_names");
            var layers = names.Length == 0
                ? Array.Empty<Layer>()
                : names.Select(name => ResolveLayer(map, name)).ToArray();
            var continueOnFailure = BridgeSupport.Bool(payload, "continue_on_failure", false);
            switch (operation)
            {
                case "inspect":
                    break;
                case "fill":
                    if (layers.Length == 0)
                        await map.FillFeatureCacheAsync(continueOnFailure);
                    else
                        await map.FillFeatureCacheAsync(layers, continueOnFailure);
                    break;
                case "clear":
                    if (layers.Length == 0)
                        await map.ClearFeatureCacheAsync(continueOnFailure);
                    else
                        await map.ClearFeatureCacheAsync(layers, continueOnFailure);
                    break;
                case "cancel":
                    await map.CancelFeatureCacheRequestsAsync(continueOnFailure);
                    break;
                default:
                    throw new ArgumentException("operation must be inspect, fill, clear, or cancel");
            }
            return (object)new
            {
                map = map.Name,
                operation,
                auto_fill = map.AutoFillFeatureCache,
                can_fill = map.CanFillFeatureCache(),
                layer_names = layers.Select(item => item.Name).ToArray(),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> CaptureThumbnailAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var output = Required(payload, "output_file");
        var width = Math.Clamp(BridgeSupport.Int(payload, "width", 800), 32, 8192);
        var height = Math.Clamp(BridgeSupport.Int(payload, "height", 600), 32, 8192);
        return await QueuedTask.Run<object>(() =>
        {
            var view = ResolveActiveView(requestedMap);
            var thumbnail = view.CaptureThumbnail(width, height);
            BitmapEncoder encoder = Path.GetExtension(output).Equals(
                ".jpg", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(output).Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                ? new JpegBitmapEncoder
                {
                    QualityLevel = Math.Clamp(BridgeSupport.Int(payload, "quality", 90), 1, 100),
                }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(thumbnail));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using (var stream = File.Create(output))
                encoder.Save(stream);
            return (object)new
            {
                map = view.Map.Name,
                output,
                width = thumbnail.PixelWidth,
                height = thumbnail.PixelHeight,
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> TransformCoordinatesAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var operation = Required(payload, "operation").ToLowerInvariant();
        var x = BridgeSupport.Double(payload, "x");
        var y = BridgeSupport.Double(payload, "y");
        return await QueuedTask.Run<object>(() =>
        {
            var view = ResolveActiveView(requestedMap);
            object result;
            switch (operation)
            {
                case "map_to_client":
                case "map_to_screen":
                {
                    var spatialReference = payload.TryGetProperty("wkid", out var wkidElement)
                        && wkidElement.ValueKind == JsonValueKind.Number
                        ? SpatialReferenceBuilder.CreateSpatialReference(wkidElement.GetInt32())
                        : view.Map.SpatialReference;
                    var mapPoint = MapPointBuilderEx.CreateMapPoint(x, y, spatialReference);
                    var point = operation == "map_to_client"
                        ? view.MapToClient(mapPoint)
                        : view.MapToScreen(mapPoint);
                    result = new
                    {
                        x = Finite(point.X),
                        y = Finite(point.Y),
                        coordinate_space = operation[7..],
                    };
                    break;
                }
                case "client_to_map":
                case "screen_to_map":
                {
                    var point = new Point(x, y);
                    var mapPoint = operation == "client_to_map"
                        ? view.ClientToMap(point)
                        : view.ScreenToMap(point);
                    result = new
                    {
                        x = Finite(mapPoint.X),
                        y = Finite(mapPoint.Y),
                        z = Finite(mapPoint.Z),
                        coordinate_space = "map",
                        spatial_reference = mapPoint.SpatialReference?.Name,
                        wkid = mapPoint.SpatialReference?.Wkid,
                    };
                    break;
                }
                case "client_to_screen":
                case "screen_to_client":
                {
                    var point = new Point(x, y);
                    var transformed = operation == "client_to_screen"
                        ? view.ClientToScreen(point)
                        : view.ScreenToClient(point);
                    result = new
                    {
                        x = Finite(transformed.X),
                        y = Finite(transformed.Y),
                        coordinate_space = operation[10..],
                    };
                    break;
                }
                default:
                    throw new ArgumentException(
                        "operation must be map_to_client, map_to_screen, client_to_map, screen_to_map, client_to_screen, or screen_to_client");
            }
            return (object)new
            {
                map = view.Map.Name,
                operation,
                input = new { x, y },
                result,
                source_data_modified = false,
            };
        });
    }

    private static object DescribeContext(MapView view)
    {
        var size = view.GetViewSize();
        var selectedDataSource = view.GetSelectedDataSourceMember();
        return new
        {
            map = view.Map.Name,
            ready = view.IsReady,
            drawing_paused = view.DrawingPaused,
            can_print = view.CanPrint,
            toc_content = view.CurrentMapTOCContent.ToString(),
            view_size = new
            {
                width = Finite(size.Width),
                height = Finite(size.Height),
            },
            has_next_camera = view.HasNextCamera(),
            has_previous_camera = view.HasPreviousCamera(),
            selected_layers = view.GetSelectedLayers().Select(item => item.Name).ToArray(),
            selected_tables = view.GetSelectedStandaloneTables().Select(item => item.Name).ToArray(),
            selected_elevation_surfaces = view.GetSelectedElevationSurfaceLayers().Select(item => item.Name).ToArray(),
            selected_elevation_sources = view.GetSelectedElevationSourceLayers().Select(item => item.Name).ToArray(),
            selected_legend_classes = view.GetSelectedLegendClasses().Select(item => item.Label).ToArray(),
            selected_data_source = selectedDataSource is null ? null : new
            {
                name = selectedDataSource.Name,
                valid = selectedDataSource.IsValid,
            },
            camera = DescribeCamera(view.Camera),
            source_data_modified = false,
        };
    }

    private static object DescribeCamera(Camera camera) => new
    {
        x = Finite(camera.X),
        y = Finite(camera.Y),
        z = Finite(camera.Z),
        scale = Finite(camera.Scale),
        heading = Finite(camera.Heading),
        pitch = Finite(camera.Pitch),
        roll = Finite(camera.Roll),
        spatial_reference = camera.SpatialReference?.Name,
    };

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;

    private static async Task OnUiAsync(
        Action callback,
        CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("ArcGIS Pro UI dispatcher is unavailable");
        if (dispatcher.CheckAccess())
        {
            callback();
            return;
        }
        await dispatcher.InvokeAsync(
            callback, DispatcherPriority.Normal, cancellationToken);
    }

    private static MapView ResolveActiveView(string? requestedMap)
    {
        var view = MapView.Active
            ?? throw new InvalidOperationException("There is no active map view");
        if (!string.IsNullOrWhiteSpace(requestedMap)
            && !string.Equals(view.Map.Name, requestedMap, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The requested map must be the active map view");
        }
        return view;
    }

    private static Layer ResolveLayer(Map map, string name)
    {
        return map.GetLayersAsFlattenedList().FirstOrDefault(item => string.Equals(
            item.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Layer not found: {name}");
    }

    private static StandaloneTable ResolveTable(Map map, string name)
    {
        return map.GetStandaloneTablesAsFlattenedList().FirstOrDefault(item => string.Equals(
            item.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Standalone table not found: {name}");
    }

    private static long[] Longs(JsonElement payload, string name, int limit)
    {
        if (!payload.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array)
            return Array.Empty<long>();
        var result = values.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Number)
            .Select(item => item.GetInt64()).Take(limit + 1).ToArray();
        if (result.Length > limit)
            throw new ArgumentException($"{name} is limited to {limit} values");
        return result;
    }

    private static long RequiredLong(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var result))
        {
            throw new ArgumentException($"{name} is required and must be an integer");
        }
        return result;
    }

    private static string Required(JsonElement payload, string name)
    {
        return BridgeSupport.String(payload, name)
            ?? throw new ArgumentException($"{name} is required");
    }
}
