using System.Globalization;
using System.IO;
using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class LayoutActions
{
    private const string RasterColorbarBindingKeyPrefix = "ArcGISCodexBridge.RasterColorbar.";
    private sealed record RendererColorStop(double Value, CIMColor Color);
    private sealed record RendererColorbarSpec(
        double Minimum,
        double Maximum,
        IReadOnlyList<RendererColorStop> Stops,
        IReadOnlyList<RendererColorStop>? Classes,
        string DefaultTitle,
        string ColorizerType);

    internal static async Task<object> CreateAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var openLayout = BridgeSupport.Bool(payload, "open_layout", true);
        Layout? createdLayout = null;
        Legend? createdLegend = null;
        HashSet<string> explicitLegendUris = new(StringComparer.OrdinalIgnoreCase);
        JsonElement createdLegendConfig = default;

        var result = await QueuedTask.Run<object>(() =>
        {
            if (Project.Current.GetItems<LayoutProjectItem>()
                .Any(item => string.Equals(item.Name, layoutName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Layout already exists: {layoutName}");
            }

            var map = BridgeSupport.ResolveMap(requestedMap);
            var pageWidth = BridgeSupport.Double(payload, "page_width", 29.7);
            var pageHeight = BridgeSupport.Double(payload, "page_height", 21.0);
            var layout = LayoutFactory.Instance.CreateLayout(
                pageWidth, pageHeight, LinearUnit.Centimeters);
            layout.SetName(layoutName);
            createdLayout = layout;

            var mapBounds = BridgeSupport.Bounds(
                payload, "map_frame_bounds", 0.8, 1.8, pageWidth - 0.8, pageHeight - 0.8);
            var mapFrameName = BridgeSupport.String(payload, "map_frame_name", "主图")!;
            var mapFrame = ElementFactory.Instance.CreateMapFrameElement(
                layout, mapBounds, map, mapFrameName, false, null);

            var extentLayerName = BridgeSupport.String(payload, "extent_layer_name");
            if (!string.IsNullOrWhiteSpace(extentLayerName))
            {
                var extentLayer = BridgeSupport.ResolveLayer(map, extentLayerName);
                mapFrame.SetCamera(extentLayer, false);
                var camera = mapFrame.Camera;
                camera.Scale *= Math.Max(1.0, BridgeSupport.Double(payload, "scale_factor", 1.05));
                mapFrame.SetCamera(camera);
            }
            else if (MapView.Active?.Map == map && MapView.Active.Camera is not null)
            {
                mapFrame.SetCamera(MapView.Active.Camera);
            }

            AddMapFrameBorder(mapFrame, payload);
            AddOptionalTitle(layout, payload, pageWidth, pageHeight);
            var legendSetup = AddOptionalLegend(
                layout, mapFrame, map, payload, pageWidth, pageHeight);
            createdLegend = legendSetup.Legend;
            explicitLegendUris = legendSetup.IncludedUris;
            createdLegendConfig = legendSetup.Config;
            AddOptionalNorthArrow(layout, mapFrame, payload, pageWidth, pageHeight);
            AddOptionalScaleBar(layout, mapFrame, payload, pageWidth);

            if (payload.TryGetProperty("ticks", out var ticks)
                && ticks.ValueKind == JsonValueKind.Object
                && BridgeSupport.Bool(ticks, "enabled", true))
            {
                AddTicksCore(layout, mapFrame, ticks);
            }
            if (payload.TryGetProperty("colorbar", out var colorbar)
                && colorbar.ValueKind == JsonValueKind.Object
                && BridgeSupport.Bool(colorbar, "enabled", true))
            {
                AddColorbarCore(layout, colorbar);
            }

            return (object)new
            {
                layout = layout.Name,
                map = map.Name,
                map_frame = mapFrame.Name,
                page_width = pageWidth,
                page_height = pageHeight,
                elements = layout.GetElementsAsFlattenedList().Select(element => new
                {
                    name = element.Name,
                    type = element.GetType().Name,
                }).ToArray(),
            };
        });

        // Legend auto-population is asynchronous in ArcGIS Pro. Let Pro create
        // its normal items first, then filter the populated CIM items by layer
        // URI. Filtering during element creation sees an empty Items array.
        if (createdLegend is not null && explicitLegendUris.Count > 0)
        {
            await FilterLegendItemsAsync(createdLegend, explicitLegendUris, createdLegendConfig);
        }

        if (openLayout && createdLayout is not null)
        {
            await await FrameworkApplication.Current.Dispatcher.InvokeAsync(
                () => FrameworkApplication.Panes.CreateLayoutPaneAsync(createdLayout));
        }
        return result;
    }

    internal static async Task<object> UpdateElementAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var elementName = BridgeSupport.String(payload, "element_name")
            ?? throw new ArgumentException("element_name is required");

        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var element = layout.FindElement(elementName)
                ?? throw new InvalidOperationException($"Layout element not found: {elementName}");
            if (BridgeSupport.Bool(payload, "delete", false))
            {
                layout.DeleteElement(element);
                return (object)new { layout = layout.Name, element = elementName, deleted = true };
            }
            if (payload.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.Number)
            {
                element.SetX(x.GetDouble());
            }
            if (payload.TryGetProperty("y", out var y) && y.ValueKind == JsonValueKind.Number)
            {
                element.SetY(y.GetDouble());
            }
            if (payload.TryGetProperty("width", out var width) && width.ValueKind == JsonValueKind.Number)
            {
                element.SetWidth(width.GetDouble());
            }
            if (payload.TryGetProperty("height", out var height) && height.ValueKind == JsonValueKind.Number)
            {
                element.SetHeight(height.GetDouble());
            }
            if (payload.TryGetProperty("rotation", out var rotation) && rotation.ValueKind == JsonValueKind.Number)
            {
                element.SetRotation(rotation.GetDouble());
            }
            if (payload.TryGetProperty("visible", out var visible)
                && visible.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                element.SetVisible(visible.GetBoolean());
            }
            return (object)new
            {
                layout = layout.Name,
                element = element.Name,
                x = element.GetX(),
                y = element.GetY(),
                width = element.GetWidth(),
                height = element.GetHeight(),
                rotation = element.GetRotation(),
                visible = element.IsVisible,
            };
        });
    }

    internal static async Task<object> DuplicateLayoutAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var newName = BridgeSupport.String(payload, "new_name")
            ?? throw new ArgumentException("new_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            if (Project.Current.GetItems<LayoutProjectItem>()
                .Any(item => string.Equals(item.Name, newName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Layout already exists: {newName}");
            }
            var source = BridgeSupport.ResolveLayout(layoutName);
            var copy = LayoutFactory.Instance.CopyLayout(source);
            copy.SetName(newName);
            return (object)new { source_layout = source.Name, layout = copy.Name };
        });
    }

    internal static async Task<object> RenameLayoutAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var newName = BridgeSupport.String(payload, "new_name")
            ?? throw new ArgumentException("new_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            if (Project.Current.GetItems<LayoutProjectItem>()
                .Any(item => string.Equals(item.Name, newName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Layout already exists: {newName}");
            }
            var layout = BridgeSupport.ResolveLayout(layoutName);
            layout.SetName(newName);
            return (object)new { old_name = layoutName, layout = layout.Name };
        });
    }

    internal static async Task<object> RemoveLayoutAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        if (!BridgeSupport.Bool(payload, "confirm_remove", false))
        {
            throw new ArgumentException("confirm_remove=true is required to remove a layout project item");
        }
        return await QueuedTask.Run<object>(() =>
        {
            var item = Project.Current.GetItems<LayoutProjectItem>()
                .FirstOrDefault(candidate => string.Equals(
                    candidate.Name, layoutName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Layout not found: {layoutName}");
            var removed = Project.Current.RemoveItem(item);
            return (object)new { layout = layoutName, removed_from_project = removed };
        });
    }

    internal static async Task<object> OpenLayoutAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var layout = await QueuedTask.Run(() => BridgeSupport.ResolveLayout(layoutName));
        await await FrameworkApplication.Current.Dispatcher.InvokeAsync(
            () => FrameworkApplication.Panes.CreateLayoutPaneAsync(layout));
        return new { layout = layout.Name, opened = true };
    }

    internal static async Task<object> AddPictureAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var picturePath = BridgeSupport.String(payload, "picture_path")
            ?? throw new ArgumentException("picture_path is required");
        var elementName = BridgeSupport.String(payload, "element_name", "图片")!;
        return await QueuedTask.Run<object>(() =>
        {
            if (!File.Exists(picturePath))
            {
                throw new FileNotFoundException("Picture file was not found.", picturePath);
            }
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var bounds = BridgeSupport.Bounds(payload, "bounds", 1, 1, 6, 6);
            var element = ElementFactory.Instance.CreatePictureGraphicElement(
                layout, bounds, picturePath, elementName, false, null);
            return (object)new
            {
                layout = layout.Name,
                element = element.Name,
                picture_path = picturePath,
                x = element.GetX(),
                y = element.GetY(),
                width = element.GetWidth(),
                height = element.GetHeight(),
            };
        });
    }

    internal static async Task<object> OrganizeElementsAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var operation = BridgeSupport.String(payload, "operation")?.ToLowerInvariant()
            ?? throw new ArgumentException("operation is required");
        var names = BridgeSupport.Strings(payload, "element_names");
        if (names.Length == 0)
        {
            throw new ArgumentException("element_names is required");
        }
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var elements = names.Select(name => layout.FindElement(name)
                ?? throw new InvalidOperationException($"Layout element not found: {name}"))
                .ToArray();
            string? createdGroup = null;
            switch (operation)
            {
                case "group":
                    var group = layout.GroupElements(elements);
                    var groupName = BridgeSupport.String(payload, "group_name");
                    if (!string.IsNullOrWhiteSpace(groupName))
                    {
                        group.SetName(groupName);
                    }
                    createdGroup = group.Name;
                    break;
                case "ungroup":
                    var groups = elements.OfType<GroupElement>().ToArray();
                    if (groups.Length != elements.Length)
                    {
                        throw new ArgumentException("ungroup requires only group elements");
                    }
                    layout.UnGroupElements(groups);
                    break;
                case "bring_to_front":
                    layout.BringToFront(elements);
                    break;
                case "send_to_back":
                    layout.SendToBack(elements);
                    break;
                case "bring_forward":
                    layout.BringForward(elements);
                    break;
                case "send_backward":
                    layout.SendBackward(elements);
                    break;
                default:
                    throw new ArgumentException($"Unsupported layout element operation: {operation}");
            }
            return (object)new
            {
                layout = layout.Name,
                operation,
                elements = names,
                group = createdGroup,
            };
        });
    }

    internal static async Task<object> ConfigureMapFrameAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var frameName = BridgeSupport.String(payload, "map_frame_name", "主图")!;
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var frame = layout.FindElement(frameName) as MapFrame
                ?? throw new InvalidOperationException($"Map frame not found: {frameName}");
            var mapName = BridgeSupport.String(payload, "map_name");
            if (!string.IsNullOrWhiteSpace(mapName))
            {
                frame.SetMap(BridgeSupport.ResolveMap(mapName));
            }
            var extentLayerName = BridgeSupport.String(payload, "extent_layer_name");
            if (!string.IsNullOrWhiteSpace(extentLayerName))
            {
                var layer = BridgeSupport.ResolveLayer(frame.Map, extentLayerName);
                frame.SetCamera(layer, BridgeSupport.Bool(payload, "use_selected_features", false));
                var camera = frame.Camera;
                camera.Scale *= Math.Max(1.0, BridgeSupport.Double(payload, "scale_factor", 1.0));
                frame.SetCamera(camera);
            }
            var bookmarkName = BridgeSupport.String(payload, "bookmark_name");
            if (!string.IsNullOrWhiteSpace(bookmarkName))
            {
                var bookmark = frame.Map.GetBookmarks().FirstOrDefault(item => string.Equals(
                    item.Name, bookmarkName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Bookmark not found: {bookmarkName}");
                frame.SetCamera(bookmark);
            }
            if (payload.TryGetProperty("rotation", out var rotation)
                && rotation.ValueKind == JsonValueKind.Number)
            {
                frame.SetRotation(rotation.GetDouble());
            }
            if (payload.TryGetProperty("locked_aspect_ratio", out var lockedAspect)
                && lockedAspect.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                frame.SetLockedAspectRatio(lockedAspect.GetBoolean());
            }
            AddMapFrameBorder(frame, payload);
            return (object)new
            {
                layout = layout.Name,
                map_frame = frame.Name,
                map = frame.Map.Name,
                rotation = frame.GetRotation(),
                locked_aspect_ratio = frame.GetLockedAspectRatio(),
                view_extent = new
                {
                    xmin = frame.GetViewExtent().XMin,
                    ymin = frame.GetViewExtent().YMin,
                    xmax = frame.GetViewExtent().XMax,
                    ymax = frame.GetViewExtent().YMax,
                },
            };
        });
    }

    internal static async Task<object> CreateMapSeriesAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var frameName = BridgeSupport.String(payload, "map_frame_name", "主图")!;
        var seriesType = BridgeSupport.String(payload, "series_type", "spatial")!.ToLowerInvariant();
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var frame = layout.FindElement(frameName) as MapFrame
                ?? throw new InvalidOperationException($"Map frame not found: {frameName}");
            MapSeries series;
            if (seriesType == "bookmark")
            {
                series = MapSeries.CreateBookmarkMapSeries(layout, frame);
            }
            else if (seriesType == "thematic")
            {
                series = MapSeries.CreateThematicMapSeries(layout, frame);
            }
            else
            {
                var indexLayerName = BridgeSupport.String(payload, "index_layer_name")
                    ?? throw new ArgumentException("index_layer_name is required for a spatial map series");
                var nameField = BridgeSupport.String(payload, "name_field")
                    ?? throw new ArgumentException("name_field is required for a spatial map series");
                var indexLayer = BridgeSupport.ResolveLayer(frame.Map, indexLayerName) as BasicFeatureLayer
                    ?? throw new InvalidOperationException($"Index feature layer not found: {indexLayerName}");
                series = MapSeries.CreateSpatialMapSeries(layout, frame, indexLayer, nameField);
            }
            var definition = series.GetDefinition();
            definition.Enabled = BridgeSupport.Bool(payload, "enabled", true);
            series.SetDefinition(definition);
            layout.SetMapSeries(series);
            var currentPage = BridgeSupport.String(payload, "current_page_number");
            if (!string.IsNullOrWhiteSpace(currentPage))
            {
                series.SetCurrentPageNumber(currentPage);
            }
            return (object)new
            {
                layout = layout.Name,
                map_frame = frame.Name,
                series_type = series.GetType().Name,
                enabled = series.Enabled,
                page_count = series.PageCount,
                current_page_name = series.CurrentPageName,
                current_page_number = series.CurrentPageNumber,
            };
        });
    }

    internal static async Task<object> GetCimAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var definition = (CIMLayout)layout.GetDefinition();
            return (object)new
            {
                layout = layout.Name,
                cim_type = definition.GetType().Name,
                cim_json = definition.ToJson(),
            };
        });
    }

    internal static async Task<object> SetCimAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!BridgeSupport.Bool(payload, "confirm_replace_definition", false))
        {
            throw new ArgumentException("confirm_replace_definition=true is required to replace a layout CIM definition");
        }
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var cimJson = BridgeSupport.String(payload, "cim_json")
            ?? throw new ArgumentException("cim_json is required");
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var definition = CIMLayout.FromJson(cimJson, new JsonDeserializationSettings());
            layout.SetDefinition(definition);
            return (object)new
            {
                layout = layout.Name,
                cim_type = definition.GetType().Name,
                definition_replaced = true,
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> AddMapFrameAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var mapName = BridgeSupport.String(payload, "map_name")
            ?? throw new ArgumentException("map_name is required");
        var mapFrameName = BridgeSupport.String(payload, "map_frame_name")
            ?? throw new ArgumentException("map_frame_name is required");

        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            if (layout.FindElement(mapFrameName) is not null)
            {
                throw new InvalidOperationException($"Layout element already exists: {mapFrameName}");
            }
            var map = BridgeSupport.ResolveMap(mapName);
            var bounds = BridgeSupport.Bounds(payload, "bounds", 1.2, 13.0, 8.5, 19.2);
            var mapFrame = ElementFactory.Instance.CreateMapFrameElement(
                layout, bounds, map, mapFrameName, false, null);

            var extentLayerName = BridgeSupport.String(payload, "extent_layer_name");
            if (!string.IsNullOrWhiteSpace(extentLayerName))
            {
                var extentLayer = BridgeSupport.ResolveLayer(map, extentLayerName);
                mapFrame.SetCamera(extentLayer, false);
                var camera = mapFrame.Camera;
                camera.Scale *= Math.Max(1.0, BridgeSupport.Double(payload, "scale_factor", 1.08));
                mapFrame.SetCamera(camera);
            }
            AddMapFrameBorder(mapFrame, payload);
            return (object)new
            {
                layout = layout.Name,
                map = map.Name,
                map_frame = mapFrame.Name,
                extent_layer = extentLayerName,
                bounds = new
                {
                    xmin = bounds.XMin,
                    ymin = bounds.YMin,
                    xmax = bounds.XMax,
                    ymax = bounds.YMax,
                },
            };
        });
    }

    internal static async Task<object> AddGraphicsAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var created = AddGraphicsCore(layout, payload);
            return (object)new { layout = layout.Name, created };
        });
    }

    internal static async Task<object> AddTicksAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var mapFrameName = BridgeSupport.String(payload, "map_frame_name", "主图")!;
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var mapFrame = layout.FindElement(mapFrameName) as MapFrame
                ?? throw new InvalidOperationException($"Map frame not found: {mapFrameName}");
            var created = AddTicksCore(layout, mapFrame, payload);
            return (object)new { layout = layout.Name, map_frame = mapFrame.Name, created };
        });
    }

    internal static async Task<object> AddColorbarAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var created = AddColorbarCore(layout, payload);
            return (object)new { layout = layout.Name, created };
        });
    }

    internal static async Task<object> AddRasterColorbarFromLayerAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");

        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = BridgeSupport.ResolveLayer(map, layerName) as BasicRasterLayer
                ?? throw new InvalidOperationException($"Raster layer not found: {layerName}");
            var colorizer = layer.GetColorizer()
                ?? throw new InvalidOperationException($"Raster layer has no colorizer: {layerName}");
            var spec = RendererColorbarSpecFromLayer(layer, colorizer, payload);
            var created = AddRendererColorbarCore(layout, payload, spec);
            var dynamicallyLinked = BridgeSupport.Bool(payload, "dynamic_linked", false);
            if (dynamicallyLinked)
            {
                StoreRasterColorbarBinding(layout, map, layer, payload);
            }
            return (object)new
            {
                layout = layout.Name,
                layer = layer.Name,
                colorizer = spec.ColorizerType,
                minimum = spec.Minimum,
                maximum = spec.Maximum,
                renderer_derived = true,
                dynamically_linked = dynamicallyLinked,
                created,
            };
        });
    }

    internal static int RefreshLinkedRasterColorbars(Map map, BasicRasterLayer layer)
    {
        var refreshed = 0;
        foreach (var item in Project.Current.GetItems<LayoutProjectItem>())
        {
            var layout = item.GetLayout();
            var definition = (CIMLayout)layout.GetDefinition();
            foreach (var property in definition.CustomProperties ?? Array.Empty<CIMStringMap>())
            {
                if (!property.Key.StartsWith(RasterColorbarBindingKeyPrefix, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(property.Value))
                {
                    continue;
                }
                using var document = JsonDocument.Parse(property.Value);
                var root = document.RootElement;
                if (!string.Equals(
                    BridgeSupport.String(root, "layer_uri"), layer.URI, StringComparison.OrdinalIgnoreCase)
                    || !root.TryGetProperty("config", out var config)
                    || config.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var prefix = BridgeSupport.String(config, "element_prefix", "绑定栅格颜色条")!;
                var oldElements = layout.GetElementsAsFlattenedList()
                    .Where(element => element.Name.StartsWith(prefix, StringComparison.Ordinal))
                    .ToArray();
                foreach (var element in oldElements)
                {
                    layout.DeleteElement(element);
                }
                var colorizer = layer.GetColorizer();
                if (colorizer is null)
                {
                    continue;
                }
                var spec = RendererColorbarSpecFromLayer(layer, colorizer, config);
                AddRendererColorbarCore(layout, config, spec);
                refreshed++;
            }
        }
        return refreshed;
    }

    internal static async Task<object> ImportAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var path = BridgeSupport.String(payload, "pagx_path")
            ?? throw new ArgumentException("pagx_path is required");
        return await QueuedTask.Run<object>(() =>
        {
            var item = ItemFactory.Instance.Create(path) as IProjectMultiItem
                ?? throw new InvalidOperationException("The file cannot be imported as a layout.");
            var imported = Project.Current.ImportItem(item).ToArray();
            return (object)new
            {
                pagx_path = path,
                imported = imported.Select(projectItem => projectItem.GetInfo().Name).ToArray(),
            };
        });
    }

    internal static async Task<object> SetTextAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var elementName = BridgeSupport.String(payload, "element_name")
            ?? throw new ArgumentException("element_name is required");
        var text = BridgeSupport.String(payload, "text", string.Empty)!;
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var element = layout.FindElement(elementName) as TextElement
                ?? throw new InvalidOperationException($"Text element not found: {elementName}");
            var properties = element.TextProperties;
            properties.Text = text;
            element.SetTextProperties(properties);
            return (object)new { layout = layout.Name, element = element.Name, text };
        });
    }

    internal static async Task<object> ZoomFrameAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var mapFrameName = BridgeSupport.String(payload, "map_frame_name", "主图")!;
        var factor = Math.Max(1.0, BridgeSupport.Double(payload, "scale_factor", 1.05));
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var mapFrame = layout.FindElement(mapFrameName) as MapFrame
                ?? throw new InvalidOperationException($"Map frame not found: {mapFrameName}");
            var layer = BridgeSupport.ResolveLayer(mapFrame.Map, layerName);
            mapFrame.SetCamera(layer, false);
            var camera = mapFrame.Camera;
            camera.Scale *= factor;
            mapFrame.SetCamera(camera);
            return (object)new
            {
                layout = layout.Name,
                map_frame = mapFrame.Name,
                layer = layer.Name,
                scale_factor = factor,
            };
        });
    }

    internal static async Task<object> SaveProjectAsAsync(JsonElement payload)
    {
        var output = BridgeSupport.String(payload, "output_aprx")
            ?? throw new ArgumentException("output_aprx is required");
        var overwrite = BridgeSupport.Bool(payload, "overwrite", false);
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? string.Empty);
        var success = await await FrameworkApplication.Current.Dispatcher.InvokeAsync(
            () => Project.Current.SaveAsAsync(output, overwrite));
        return new
        {
            output_aprx = output,
            success,
            current_project_switched_to_copy = success,
        };
    }

    private static void AddMapFrameBorder(MapFrame mapFrame, JsonElement payload)
    {
        var definition = (CIMMapFrame)mapFrame.GetDefinition();
        definition.GraphicFrame ??= new CIMGraphicFrame();
        if (!BridgeSupport.Bool(payload, "map_frame_border", true))
        {
            definition.GraphicFrame.BorderSymbol = null;
            mapFrame.SetDefinition(definition);
            return;
        }
        var symbol = SymbolFactory.Instance.ConstructLineSymbol(
            BridgeSupport.Color(payload, "map_frame_border_color", ColorFactory.Instance.CreateRGBColor(35, 35, 35)),
            BridgeSupport.Double(payload, "map_frame_border_width", 0.8),
            SimpleLineStyle.Solid);
        definition.GraphicFrame.BorderSymbol = symbol.MakeSymbolReference();
        definition.GraphicFrame.BorderGapX = 0;
        definition.GraphicFrame.BorderGapY = 0;
        mapFrame.SetDefinition(definition);
    }

    private static void AddOptionalTitle(
        Layout layout,
        JsonElement payload,
        double pageWidth,
        double pageHeight)
    {
        var title = BridgeSupport.String(payload, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }
        var config = payload.TryGetProperty("title_symbol", out var titleSymbol)
            && titleSymbol.ValueKind == JsonValueKind.Object
            ? titleSymbol
            : default;
        var symbol = BridgeSupport.TextSymbol(config);
        if (config.ValueKind != JsonValueKind.Object)
        {
            symbol.FontStyleName = "Bold";
            symbol.Height = 18;
        }
        symbol.HorizontalAlignment = HorizontalAlignment.Center;
        symbol.VerticalAlignment = VerticalAlignment.Center;
        ElementFactory.Instance.CreateTextGraphicElement(
            layout,
            TextType.PointText,
            MapPointBuilderEx.CreateMapPoint(
                BridgeSupport.Double(payload, "title_x", pageWidth / 2),
                BridgeSupport.Double(payload, "title_y", pageHeight - 0.55)),
            symbol,
            title,
            "标题",
            false,
            null);
    }

    private static (Legend? Legend, HashSet<string> IncludedUris, JsonElement Config) AddOptionalLegend(
        Layout layout,
        MapFrame mapFrame,
        Map map,
        JsonElement payload,
        double pageWidth,
        double pageHeight)
    {
        if (payload.TryGetProperty("legend", out var legendConfig)
            && legendConfig.ValueKind == JsonValueKind.False)
        {
            return (null, new HashSet<string>(StringComparer.OrdinalIgnoreCase), default);
        }
        legendConfig = legendConfig.ValueKind == JsonValueKind.Object ? legendConfig : default;
        if (!BridgeSupport.Bool(legendConfig, "enabled", true))
        {
            return (null, new HashSet<string>(StringComparer.OrdinalIgnoreCase), default);
        }
        var bounds = BridgeSupport.Bounds(
            legendConfig, "bounds", pageWidth - 6.2, 2.4, pageWidth - 1.1, 8.5);

        if (BridgeSupport.Bool(legendConfig, "background", true))
        {
            var background = SymbolFactory.Instance.ConstructPolygonSymbol(
                BridgeSupport.Color(legendConfig, "background_color", ColorFactory.Instance.CreateRGBColor(255, 255, 255, 88)),
                SimpleFillStyle.Solid,
                SymbolFactory.Instance.ConstructStroke(
                    BridgeSupport.Color(legendConfig, "border_color", ColorFactory.Instance.CreateRGBColor(90, 90, 90, 70)),
                    BridgeSupport.Double(legendConfig, "border_width", 0.5),
                    SimpleLineStyle.Solid));
            ElementFactory.Instance.CreatePredefinedShapeGraphicElement(
                layout, PredefinedShape.Rectangle, bounds, background, "图例背景", false, null);
        }

        var legend = ElementFactory.Instance.CreateMapSurroundElement(
            layout,
            bounds,
            new LegendInfo { MapFrameName = mapFrame.Name },
            "图例",
            false,
            null) as Legend;
        if (legend is null)
        {
            return (null, new HashSet<string>(StringComparer.OrdinalIgnoreCase), default);
        }
        var definition = (CIMLegend)legend.GetDefinition();
        definition.Title = BridgeSupport.String(legendConfig, "title", "图例");
        definition.ShowTitle = BridgeSupport.Bool(legendConfig, "show_title", true);
        definition.AutoFonts = true;
        definition.AutoVisibility = false;
        definition.FittingStrategy = LegendFittingStrategy.AdjustFrame;
        var includedNames = BridgeSupport.Strings(legendConfig, "layer_names")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var includedUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (includedNames.Count > 0)
        {
            includedUris = map.GetLayersAsFlattenedList()
                .Where(layer => includedNames.Contains(layer.Name))
                .Select(layer => layer.URI)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        definition.AutoAdd = true;
        definition.ExcludedLayers = Array.Empty<string>();
        legend.SetDefinition(definition);
        return (legend, includedUris, legendConfig.Clone());
    }

    private static async Task FilterLegendItemsAsync(
        Legend legend,
        HashSet<string> includedUris,
        JsonElement config)
    {
        const int attempts = 20;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var populated = await QueuedTask.Run(() =>
            {
                var definition = (CIMLegend)legend.GetDefinition();
                return definition.Items is { Length: > 0 };
            });
            if (populated)
            {
                break;
            }
            await Task.Delay(100);
        }

        await QueuedTask.Run(() =>
        {
            var definition = (CIMLegend)legend.GetDefinition();
            if (definition.Items is not { Length: > 0 })
            {
                throw new InvalidOperationException(
                    "ArcGIS Pro did not populate legend items within the expected time.");
            }
            definition.AutoAdd = false;
            definition.Items = definition.Items
                .Where(item => includedUris.Contains(item.Layer))
                .Select(item => ConfigureLegendItem(item, config))
                .ToArray();
            legend.SetDefinition(definition);
        });
    }

    private static CIMLegendItem ConfigureLegendItem(CIMLegendItem source, JsonElement config)
    {
        var item = source;

        item.AutoVisibility = false;
        item.IsVisible = true;
        item.ShowLayerName = BridgeSupport.Bool(config, "show_layer_name", false);
        item.ShowGroupLayerName = BridgeSupport.Bool(config, "show_group_layer_name", false);
        item.ShowHeading = BridgeSupport.Bool(config, "show_heading", true);
        item.ShowLabels = BridgeSupport.Bool(config, "show_labels", true);
        item.ShowDescription = BridgeSupport.Bool(config, "show_description", false);
        item.ShowCounts = BridgeSupport.Bool(config, "show_counts", false);
        item.PatchWidth = BridgeSupport.Double(config, "patch_width", item.PatchWidth);
        item.PatchHeight = BridgeSupport.Double(config, "patch_height", item.PatchHeight);
        item.ScaleToPatch = BridgeSupport.Bool(config, "scale_to_patch", true);

        if (config.TryGetProperty("text_symbol", out var textConfig)
            && textConfig.ValueKind == JsonValueKind.Object)
        {
            var symbol = BridgeSupport.TextSymbol(textConfig).MakeSymbolReference();
            item.LabelSymbol = symbol;
            item.HeadingSymbol = symbol;
            item.LayerNameSymbol = symbol;
            item.DescriptionSymbol = symbol;
        }
        return item;
    }

    private static void StoreRasterColorbarBinding(
        Layout layout,
        Map map,
        BasicRasterLayer layer,
        JsonElement config)
    {
        var prefix = BridgeSupport.String(config, "element_prefix", "绑定栅格颜色条")!;
        var key = RasterColorbarBindingKeyPrefix + prefix;
        var value = JsonSerializer.Serialize(new
        {
            map_uri = map.URI,
            layer_uri = layer.URI,
            map_name = map.Name,
            layer_name = layer.Name,
            config = config.Clone(),
        });
        var definition = (CIMLayout)layout.GetDefinition();
        var properties = (definition.CustomProperties ?? Array.Empty<CIMStringMap>())
            .Where(property => !string.Equals(property.Key, key, StringComparison.Ordinal))
            .ToList();
        properties.Add(new CIMStringMap { Key = key, Value = value });
        definition.CustomProperties = properties.ToArray();
        layout.SetDefinition(definition);
    }

    private static void AddOptionalNorthArrow(
        Layout layout,
        MapFrame mapFrame,
        JsonElement payload,
        double pageWidth,
        double pageHeight)
    {
        if (payload.TryGetProperty("north_arrow", out var config)
            && config.ValueKind == JsonValueKind.False)
        {
            return;
        }
        config = config.ValueKind == JsonValueKind.Object ? config : default;
        if (!BridgeSupport.Bool(config, "enabled", true))
        {
            return;
        }
        ElementFactory.Instance.CreateMapSurroundElement(
            layout,
            BridgeSupport.Bounds(config, "bounds", pageWidth - 3.6, pageHeight - 4.0, pageWidth - 1.2, pageHeight - 1.3),
            new NorthArrowInfo { MapFrameName = mapFrame.Name },
            "指北针",
            false,
            null);
    }

    private static void AddOptionalScaleBar(
        Layout layout,
        MapFrame mapFrame,
        JsonElement payload,
        double pageWidth)
    {
        if (payload.TryGetProperty("scale_bar", out var config)
            && config.ValueKind == JsonValueKind.False)
        {
            return;
        }
        config = config.ValueKind == JsonValueKind.Object ? config : default;
        if (!BridgeSupport.Bool(config, "enabled", true))
        {
            return;
        }
        var scaleBar = ElementFactory.Instance.CreateMapSurroundElement(
            layout,
            BridgeSupport.Bounds(config, "bounds", pageWidth / 2 - 3.5, 0.45, pageWidth / 2 + 3.5, 1.45),
            new ScaleBarInfo { MapFrameName = mapFrame.Name },
            "比例尺",
            false,
            null) as ScaleBar;
        if (scaleBar is null)
        {
            return;
        }
        var definition = (CIMScaleBar)scaleBar.GetDefinition();
        var units = BridgeSupport.String(config, "units", "Kilometers")!.ToLowerInvariant();
        definition.Units = units switch
        {
            "meters" or "meter" or "m" => LinearUnit.Meters,
            "miles" or "mile" or "mi" => LinearUnit.Miles,
            "feet" or "foot" or "ft" => LinearUnit.Feet,
            _ => LinearUnit.Kilometers,
        };
        definition.UnitLabel = BridgeSupport.String(config, "unit_label", "km");
        definition.Divisions = Math.Clamp(BridgeSupport.Int(config, "divisions", 4), 1, 10);
        definition.Subdivisions = Math.Clamp(BridgeSupport.Int(config, "subdivisions", 2), 0, 10);
        var divisionValue = BridgeSupport.Double(config, "division_value", 0);
        if (divisionValue > 0)
        {
            definition.Division = divisionValue;
            // Keep the requested cartographic value (for example 50 or 100 km)
            // and let Pro resize the scale-bar frame around it.
            definition.FittingStrategy = ScaleBarFittingStrategy.AdjustFrame;
        }
        else if (Enum.TryParse<ScaleBarFittingStrategy>(
            BridgeSupport.String(config, "fitting_strategy"), true, out var fittingStrategy))
        {
            definition.FittingStrategy = fittingStrategy;
        }
        else
        {
            definition.FittingStrategy = ScaleBarFittingStrategy.AdjustDivision;
        }
        scaleBar.SetDefinition(definition);
    }

    private static string[] AddGraphicsCore(Layout layout, JsonElement payload)
    {
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
                ?? throw new ArgumentException("Each layout graphic requires a type");
            var name = BridgeSupport.String(item, "name", $"Layout Graphic {index}")!;
            if (type == "text")
            {
                var point = item.TryGetProperty("position", out var position)
                    ? Position(position)
                    : new Coordinate2D(1, 1);
                var config = item.TryGetProperty("text_symbol", out var textSymbol)
                    && textSymbol.ValueKind == JsonValueKind.Object
                    ? textSymbol
                    : default;
                var element = ElementFactory.Instance.CreateTextGraphicElement(
                    layout,
                    TextType.PointText,
                    MapPointBuilderEx.CreateMapPoint(point.X, point.Y),
                    BridgeSupport.TextSymbol(config),
                    BridgeSupport.String(item, "text", string.Empty),
                    name,
                    false,
                    null);
                if (item.TryGetProperty("rotation", out var rotation)
                    && rotation.ValueKind == JsonValueKind.Number)
                {
                    element.SetRotation(rotation.GetDouble());
                }
            }
            else if (type is "rectangle" or "box")
            {
                var bounds = BridgeSupport.Bounds(item, "bounds", 1, 1, 3, 2);
                ElementFactory.Instance.CreatePredefinedShapeGraphicElement(
                    layout,
                    PredefinedShape.Rectangle,
                    bounds,
                    PolygonSymbol(item),
                    name,
                    false,
                    null);
            }
            else if (type is "line" or "polyline")
            {
                var points = PageCoordinates(item);
                ElementFactory.Instance.CreateGraphicElement(
                    layout,
                    PolylineBuilderEx.CreatePolyline(points),
                    LineSymbol(item),
                    name,
                    false,
                    null);
            }
            else
            {
                throw new ArgumentException($"Unsupported layout graphic type: {type}");
            }
            created.Add(name);
        }
        return created.ToArray();
    }

    private static string[] AddTicksCore(Layout layout, MapFrame mapFrame, JsonElement config)
    {
        var frameExtent = mapFrame.GetGeometry().Extent;
        var viewExtent = mapFrame.GetViewExtent();
        var xCount = Math.Clamp(BridgeSupport.Int(config, "x_intervals", 5), 2, 12);
        var yCount = Math.Clamp(BridgeSupport.Int(config, "y_intervals", 5), 2, 12);
        var length = BridgeSupport.Double(config, "tick_length", 0.12);
        var offset = BridgeSupport.Double(config, "label_offset", 0.18);
        var lineColor = BridgeSupport.Color(config, "color", ColorFactory.Instance.CreateRGBColor(45, 45, 45));
        // Use page-anchored text glyphs as tick marks. ArcGIS Pro can normalize
        // very small line/rectangle graphics against the map-frame view and turn
        // them into full internal grid lines. Text glyphs remain page elements,
        // so they are guaranteed to stay outside the frame.
        var markSize = Math.Max(4.0, BridgeSupport.Double(config, "mark_size", length * 36.0));
        var markSymbol = SymbolFactory.Instance.ConstructTextSymbol(
            lineColor, markSize, "Arial", "Regular");
        markSymbol.HorizontalAlignment = HorizontalAlignment.Center;
        markSymbol.VerticalAlignment = VerticalAlignment.Center;
        var textConfig = config.TryGetProperty("text_symbol", out var textSymbol)
            && textSymbol.ValueKind == JsonValueKind.Object
            ? textSymbol
            : default;
        var labelSymbol = BridgeSupport.TextSymbol(textConfig);
        if (textConfig.ValueKind != JsonValueKind.Object)
        {
            labelSymbol.Height = 6.5;
        }
        var geographic = viewExtent.SpatialReference?.IsGeographic ?? false;
        var created = new List<string>();

        for (var index = 0; index <= xCount; index++)
        {
            var ratio = index / (double)xCount;
            var pageX = frameExtent.XMin + ratio * frameExtent.Width;
            var value = viewExtent.XMin + ratio * viewExtent.Width;
            foreach (var (y1, y2, suffix) in new[]
            {
                (frameExtent.YMin, frameExtent.YMin - length, "B"),
                (frameExtent.YMax, frameExtent.YMax + length, "T"),
            })
            {
                var name = $"刻度_X_{index}_{suffix}";
                ElementFactory.Instance.CreateTextGraphicElement(
                    layout,
                    TextType.PointText,
                    MapPointBuilderEx.CreateMapPoint(pageX, (y1 + y2) / 2),
                    markSymbol,
                    "|",
                    name,
                    false,
                    null);
                created.Add(name);
            }
            var labelName = $"刻度标注_X_{index}";
            var label = BridgeSupport.FormatCoordinate(value, geographic, true);
            var symbol = labelSymbol.Clone() as CIMTextSymbol ?? labelSymbol;
            symbol.HorizontalAlignment = HorizontalAlignment.Center;
            symbol.VerticalAlignment = VerticalAlignment.Top;
            ElementFactory.Instance.CreateTextGraphicElement(
                layout,
                TextType.PointText,
                MapPointBuilderEx.CreateMapPoint(pageX, frameExtent.YMin - length - offset),
                symbol,
                label,
                labelName,
                false,
                null);
            created.Add(labelName);
        }

        for (var index = 0; index <= yCount; index++)
        {
            var ratio = index / (double)yCount;
            var pageY = frameExtent.YMin + ratio * frameExtent.Height;
            var value = viewExtent.YMin + ratio * viewExtent.Height;
            foreach (var (x1, x2, suffix) in new[]
            {
                (frameExtent.XMin, frameExtent.XMin - length, "L"),
                (frameExtent.XMax, frameExtent.XMax + length, "R"),
            })
            {
                var name = $"刻度_Y_{index}_{suffix}";
                ElementFactory.Instance.CreateTextGraphicElement(
                    layout,
                    TextType.PointText,
                    MapPointBuilderEx.CreateMapPoint((x1 + x2) / 2, pageY),
                    markSymbol,
                    "-",
                    name,
                    false,
                    null);
                created.Add(name);
            }
            var labelName = $"刻度标注_Y_{index}";
            var label = BridgeSupport.FormatCoordinate(value, geographic, false);
            var symbol = labelSymbol.Clone() as CIMTextSymbol ?? labelSymbol;
            symbol.HorizontalAlignment = HorizontalAlignment.Right;
            symbol.VerticalAlignment = VerticalAlignment.Center;
            ElementFactory.Instance.CreateTextGraphicElement(
                layout,
                TextType.PointText,
                MapPointBuilderEx.CreateMapPoint(frameExtent.XMin - length - offset, pageY),
                symbol,
                label,
                labelName,
                false,
                null);
            created.Add(labelName);
        }
        return created.ToArray();
    }

    private static RendererColorbarSpec RendererColorbarSpecFromLayer(
        BasicRasterLayer layer,
        CIMRasterColorizer colorizer,
        JsonElement config)
    {
        if (colorizer is CIMRasterClassifyColorizer classify
            && classify.ClassBreaks is { Length: > 0 })
        {
            var classifyMinimum = classify.MinimumBreak;
            var classes = classify.ClassBreaks
                .Select(item => new RendererColorStop(item.UpperBound, Rgb(item.Color)))
                .ToArray();
            var stops = new List<RendererColorStop>
            {
                new(classifyMinimum, classes[0].Color),
            };
            stops.AddRange(classes);
            return new RendererColorbarSpec(
                classifyMinimum,
                classes[^1].Value,
                stops,
                classes,
                string.IsNullOrWhiteSpace(classify.Heading) ? layer.Name : classify.Heading,
                colorizer.GetType().Name);
        }

        CIMColorRamp? ramp = null;
        double? rendererMinimum = null;
        double? rendererMaximum = null;
        string? heading = null;
        if (colorizer is CIMRasterStretchColorizer stretch)
        {
            ramp = stretch.ColorRamp;
            heading = stretch.Heading;
            if (stretch.UseCustomStretchMinMax)
            {
                rendererMinimum = stretch.CustomStretchMin;
                rendererMaximum = stretch.CustomStretchMax;
            }
        }
        else if (colorizer is CIMRasterShadedReliefColorizer relief)
        {
            ramp = relief.ColorRamp;
        }

        var minimum = config.TryGetProperty("minimum", out var requestedMinimum)
            && requestedMinimum.ValueKind == JsonValueKind.Number
            ? requestedMinimum.GetDouble()
            : rendererMinimum;
        var maximum = config.TryGetProperty("maximum", out var requestedMaximum)
            && requestedMaximum.ValueKind == JsonValueKind.Number
            ? requestedMaximum.GetDouble()
            : rendererMaximum;
        if (minimum is null || maximum is null || maximum <= minimum)
        {
            throw new ArgumentException(
                "minimum and maximum are required unless the stretch renderer uses custom min/max.");
        }
        var normalizedStops = RampStops(ramp);
        var valueStops = normalizedStops
            .Select(item => new RendererColorStop(
                minimum.Value + item.Ratio * (maximum.Value - minimum.Value),
                item.Color))
            .ToArray();
        return new RendererColorbarSpec(
            minimum.Value,
            maximum.Value,
            valueStops,
            null,
            string.IsNullOrWhiteSpace(heading) ? layer.Name : heading,
            colorizer.GetType().Name);
    }

    private static string[] AddRendererColorbarCore(
        Layout layout,
        JsonElement config,
        RendererColorbarSpec spec)
    {
        var bounds = BridgeSupport.Bounds(config, "bounds", 4.0, 0.8, 12.0, 1.25);
        var orientation = BridgeSupport.String(config, "orientation", "horizontal")!.ToLowerInvariant();
        var prefix = BridgeSupport.String(config, "element_prefix", "栅格颜色条")!;
        var created = new List<string>();
        if (spec.Classes is { Count: > 0 })
        {
            var lower = spec.Minimum;
            for (var index = 0; index < spec.Classes.Count; index++)
            {
                var item = spec.Classes[index];
                var start = Ratio(lower, spec.Minimum, spec.Maximum);
                var end = Ratio(item.Value, spec.Minimum, spec.Maximum);
                created.Add(CreateColorbarCell(
                    layout, bounds, orientation, start, end, item.Color, $"{prefix}_{index + 1}"));
                lower = item.Value;
            }
        }
        else
        {
            var samples = Math.Clamp(BridgeSupport.Int(config, "samples", 64), 16, 256);
            for (var index = 0; index < samples; index++)
            {
                var start = index / (double)samples;
                var end = (index + 1) / (double)samples;
                var color = InterpolateStops(spec.Stops, spec.Minimum + (start + end) / 2 * (spec.Maximum - spec.Minimum));
                created.Add(CreateColorbarCell(
                    layout, bounds, orientation, start, end, color, $"{prefix}_{index + 1}"));
            }
        }

        var border = SymbolFactory.Instance.ConstructPolygonSymbol(
            ColorFactory.Instance.CreateRGBColor(255, 255, 255, 0),
            SimpleFillStyle.Solid,
            SymbolFactory.Instance.ConstructStroke(
                BridgeSupport.Color(config, "border_color", ColorFactory.Instance.CreateRGBColor(45, 45, 45)),
                BridgeSupport.Double(config, "border_width", 0.45),
                SimpleLineStyle.Solid));
        ElementFactory.Instance.CreatePredefinedShapeGraphicElement(
            layout, PredefinedShape.Rectangle, bounds, border, $"{prefix}_边框", false, null);
        created.Add($"{prefix}_边框");

        var textConfig = config.TryGetProperty("text_symbol", out var textSymbol)
            && textSymbol.ValueKind == JsonValueKind.Object
            ? textSymbol
            : default;
        var symbolText = BridgeSupport.TextSymbol(textConfig);
        if (textConfig.ValueKind != JsonValueKind.Object)
        {
            symbolText.Height = 7;
        }
        var title = BridgeSupport.String(config, "title", spec.DefaultTitle);
        if (!string.IsNullOrWhiteSpace(title))
        {
            var titleSymbol = symbolText.Clone() as CIMTextSymbol ?? symbolText;
            titleSymbol.FontStyleName = "Bold";
            ElementFactory.Instance.CreateTextGraphicElement(
                layout,
                TextType.PointText,
                MapPointBuilderEx.CreateMapPoint(bounds.XMin, bounds.YMax + 0.3),
                titleSymbol,
                title,
                $"{prefix}_标题",
                false,
                null);
            created.Add($"{prefix}_标题");
        }

        var requestedValues = BridgeSupport.Doubles(config, "label_values");
        var requestedLabels = BridgeSupport.Strings(config, "labels");
        double[] values;
        if (requestedValues.Length > 0)
        {
            values = requestedValues
                .Where(value => value >= spec.Minimum && value <= spec.Maximum)
                .ToArray();
        }
        else if (spec.Classes is { Count: > 0 })
        {
            values = new[] { spec.Minimum }.Concat(spec.Classes.Select(item => item.Value)).ToArray();
        }
        else
        {
            var count = Math.Clamp(BridgeSupport.Int(config, "label_count", 5), 2, 12);
            values = Enumerable.Range(0, count)
                .Select(index => spec.Minimum + (spec.Maximum - spec.Minimum) * index / (count - 1))
                .ToArray();
        }
        for (var index = 0; index < values.Length; index++)
        {
            var ratio = Ratio(values[index], spec.Minimum, spec.Maximum);
            var x = orientation == "horizontal" ? bounds.XMin + ratio * bounds.Width : bounds.XMax + 0.18;
            var y = orientation == "horizontal" ? bounds.YMin - 0.2 : bounds.YMin + ratio * bounds.Height;
            var labelSymbol = symbolText.Clone() as CIMTextSymbol ?? symbolText;
            labelSymbol.HorizontalAlignment = orientation == "horizontal"
                ? HorizontalAlignment.Center
                : HorizontalAlignment.Left;
            labelSymbol.VerticalAlignment = orientation == "horizontal"
                ? VerticalAlignment.Top
                : VerticalAlignment.Center;
            var label = index < requestedLabels.Length
                ? requestedLabels[index]
                : values[index].ToString(BridgeSupport.String(config, "number_format", "0"), CultureInfo.InvariantCulture);
            var name = $"{prefix}_标注_{index + 1}";
            ElementFactory.Instance.CreateTextGraphicElement(
                layout,
                TextType.PointText,
                MapPointBuilderEx.CreateMapPoint(x, y),
                labelSymbol,
                label,
                name,
                false,
                null);
            created.Add(name);
        }
        return created.ToArray();
    }

    private static string CreateColorbarCell(
        Layout layout,
        Envelope bounds,
        string orientation,
        double start,
        double end,
        CIMColor color,
        string name)
    {
        var cell = orientation == "horizontal"
            ? EnvelopeBuilderEx.CreateEnvelope(
                bounds.XMin + start * bounds.Width, bounds.YMin,
                bounds.XMin + end * bounds.Width, bounds.YMax)
            : EnvelopeBuilderEx.CreateEnvelope(
                bounds.XMin, bounds.YMin + start * bounds.Height,
                bounds.XMax, bounds.YMin + end * bounds.Height);
        var symbol = SymbolFactory.Instance.ConstructPolygonSymbol(
            color,
            SimpleFillStyle.Solid,
            SymbolFactory.Instance.ConstructStroke(
                ColorFactory.Instance.CreateRGBColor(255, 255, 255, 0), 0, SimpleLineStyle.Solid));
        ElementFactory.Instance.CreatePredefinedShapeGraphicElement(
            layout, PredefinedShape.Rectangle, cell, symbol, name, false, null);
        return name;
    }

    private static double Ratio(double value, double minimum, double maximum)
    {
        return Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);
    }

    private static CIMColor InterpolateStops(
        IReadOnlyList<RendererColorStop> stops,
        double value)
    {
        if (stops.Count == 0)
        {
            return ColorFactory.Instance.CreateRGBColor(128, 128, 128);
        }
        if (value <= stops[0].Value)
        {
            return stops[0].Color;
        }
        for (var index = 1; index < stops.Count; index++)
        {
            if (value > stops[index].Value)
            {
                continue;
            }
            var left = stops[index - 1];
            var right = stops[index];
            var ratio = right.Value <= left.Value ? 0 : (value - left.Value) / (right.Value - left.Value);
            var leftValues = left.Color.Values ?? new[] { 128.0, 128.0, 128.0, 100.0 };
            var rightValues = right.Color.Values ?? leftValues;
            return ColorFactory.Instance.CreateRGBColor(
                Channel(leftValues, rightValues, 0, ratio),
                Channel(leftValues, rightValues, 1, ratio),
                Channel(leftValues, rightValues, 2, ratio),
                Channel(leftValues, rightValues, 3, ratio, 100));
        }
        return stops[^1].Color;
    }

    private static double Channel(
        double[] left,
        double[] right,
        int index,
        double ratio,
        double fallback = 0)
    {
        var leftValue = index < left.Length ? left[index] : fallback;
        var rightValue = index < right.Length ? right[index] : leftValue;
        return leftValue + (rightValue - leftValue) * ratio;
    }

    private static List<(double Ratio, CIMColor Color)> RampStops(CIMColorRamp? ramp)
    {
        if (ramp is CIMMultipartColorRamp multipart
            && multipart.ColorRamps is { Length: > 0 })
        {
            var weights = multipart.Weights is { Length: > 0 }
                ? multipart.Weights
                : Enumerable.Repeat(1.0, multipart.ColorRamps.Length).ToArray();
            var total = weights.Take(multipart.ColorRamps.Length).Sum();
            if (total <= 0)
            {
                total = multipart.ColorRamps.Length;
                weights = Enumerable.Repeat(1.0, multipart.ColorRamps.Length).ToArray();
            }
            var result = new List<(double Ratio, CIMColor Color)>();
            var offset = 0.0;
            for (var index = 0; index < multipart.ColorRamps.Length; index++)
            {
                var weight = index < weights.Length ? weights[index] : 1.0;
                var local = RampStops(multipart.ColorRamps[index]);
                foreach (var item in local)
                {
                    var ratio = (offset + item.Ratio * weight) / total;
                    if (result.Count == 0 || ratio > result[^1].Ratio)
                    {
                        result.Add((ratio, item.Color));
                    }
                    else if (Math.Abs(ratio - result[^1].Ratio) < 1e-9)
                    {
                        result[^1] = (ratio, item.Color);
                    }
                }
                offset += weight;
            }
            return result;
        }
        if (ramp is CIMContinuousColorRamp continuous)
        {
            return new List<(double Ratio, CIMColor Color)>
            {
                (0, Rgb(continuous.FromColor)),
                (1, Rgb(continuous.ToColor)),
            };
        }
        if (ramp is CIMFixedColorRamp fixedRamp
            && fixedRamp.Colors is { Length: > 0 })
        {
            return fixedRamp.Colors.Select((color, index) =>
                (Ratio: fixedRamp.Colors.Length == 1 ? 0 : index / (double)(fixedRamp.Colors.Length - 1),
                 Color: (CIMColor)Rgb(color))).ToList();
        }
        return new List<(double Ratio, CIMColor Color)>
        {
            (0, ColorFactory.Instance.CreateRGBColor(245, 242, 225)),
            (1, ColorFactory.Instance.CreateRGBColor(46, 107, 79)),
        };
    }

    private static CIMRGBColor Rgb(CIMColor color)
    {
        return ColorFactory.Instance.ConvertToRGB(color);
    }

    private static string[] AddColorbarCore(Layout layout, JsonElement config)
    {
        var bounds = BridgeSupport.Bounds(config, "bounds", 1.2, 1.0, 2.0, 7.0);
        var orientation = BridgeSupport.String(config, "orientation", "vertical")!.ToLowerInvariant();
        var colors = ReadColors(config);
        if (colors.Count < 2)
        {
            colors = new List<CIMColor>
            {
                ColorFactory.Instance.CreateRGBColor(245, 242, 225),
                ColorFactory.Instance.CreateRGBColor(46, 107, 79),
            };
        }
        var minimum = BridgeSupport.Double(config, "minimum", 0);
        var maximum = BridgeSupport.Double(config, "maximum", 100);
        var labels = BridgeSupport.Strings(config, "labels");
        var created = new List<string>();

        for (var index = 0; index < colors.Count; index++)
        {
            Envelope cell;
            if (orientation == "horizontal")
            {
                var width = bounds.Width / colors.Count;
                cell = EnvelopeBuilderEx.CreateEnvelope(
                    bounds.XMin + index * width, bounds.YMin,
                    bounds.XMin + (index + 1) * width, bounds.YMax);
            }
            else
            {
                var height = bounds.Height / colors.Count;
                cell = EnvelopeBuilderEx.CreateEnvelope(
                    bounds.XMin, bounds.YMin + index * height,
                    bounds.XMax, bounds.YMin + (index + 1) * height);
            }
            var symbol = SymbolFactory.Instance.ConstructPolygonSymbol(
                colors[index],
                SimpleFillStyle.Solid,
                SymbolFactory.Instance.ConstructStroke(
                    ColorFactory.Instance.CreateRGBColor(255, 255, 255, 0), 0, SimpleLineStyle.Solid));
            var name = $"颜色条_{index + 1}";
            ElementFactory.Instance.CreatePredefinedShapeGraphicElement(
                layout, PredefinedShape.Rectangle, cell, symbol, name, false, null);
            created.Add(name);
        }

        var border = SymbolFactory.Instance.ConstructPolygonSymbol(
            ColorFactory.Instance.CreateRGBColor(255, 255, 255, 0),
            SimpleFillStyle.Solid,
            SymbolFactory.Instance.ConstructStroke(
                BridgeSupport.Color(config, "border_color", ColorFactory.Instance.CreateRGBColor(45, 45, 45)),
                BridgeSupport.Double(config, "border_width", 0.5),
                SimpleLineStyle.Solid));
        ElementFactory.Instance.CreatePredefinedShapeGraphicElement(
            layout, PredefinedShape.Rectangle, bounds, border, "颜色条边框", false, null);
        created.Add("颜色条边框");

        var textConfig = config.TryGetProperty("text_symbol", out var textSymbol)
            && textSymbol.ValueKind == JsonValueKind.Object
            ? textSymbol
            : default;
        var symbolText = BridgeSupport.TextSymbol(textConfig);
        if (textConfig.ValueKind != JsonValueKind.Object)
        {
            symbolText.Height = 7;
        }
        var title = BridgeSupport.String(config, "title");
        if (!string.IsNullOrWhiteSpace(title))
        {
            var titleSymbol = symbolText.Clone() as CIMTextSymbol ?? symbolText;
            titleSymbol.FontStyleName = "Bold";
            ElementFactory.Instance.CreateTextGraphicElement(
                layout,
                TextType.PointText,
                MapPointBuilderEx.CreateMapPoint(bounds.XMin, bounds.YMax + 0.3),
                titleSymbol,
                title,
                "颜色条标题",
                false,
                null);
            created.Add("颜色条标题");
        }

        var labelValues = labels.Length > 0
            ? labels
            : Enumerable.Range(0, colors.Count + 1)
                .Select(index => (minimum + (maximum - minimum) * index / colors.Count)
                    .ToString("0", CultureInfo.InvariantCulture))
                .ToArray();
        for (var index = 0; index < labelValues.Length; index++)
        {
            var ratio = labelValues.Length == 1 ? 0 : index / (double)(labelValues.Length - 1);
            var x = orientation == "horizontal" ? bounds.XMin + ratio * bounds.Width : bounds.XMax + 0.18;
            var y = orientation == "horizontal" ? bounds.YMin - 0.2 : bounds.YMin + ratio * bounds.Height;
            var labelSymbol = symbolText.Clone() as CIMTextSymbol ?? symbolText;
            labelSymbol.HorizontalAlignment = orientation == "horizontal"
                ? HorizontalAlignment.Center
                : HorizontalAlignment.Left;
            labelSymbol.VerticalAlignment = orientation == "horizontal"
                ? VerticalAlignment.Top
                : VerticalAlignment.Center;
            var name = $"颜色条标注_{index + 1}";
            ElementFactory.Instance.CreateTextGraphicElement(
                layout,
                TextType.PointText,
                MapPointBuilderEx.CreateMapPoint(x, y),
                labelSymbol,
                labelValues[index],
                name,
                false,
                null);
            created.Add(name);
        }
        return created.ToArray();
    }

    private static List<CIMColor> ReadColors(JsonElement config)
    {
        if (!config.TryGetProperty("color_stops", out var stops)
            || stops.ValueKind != JsonValueKind.Array)
        {
            return new List<CIMColor>();
        }
        return stops.EnumerateArray().Select(item =>
        {
            return item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("color", out var color)
                ? BridgeSupport.Color(color)
                : BridgeSupport.Color(item);
        }).ToList();
    }

    private static Coordinate2D Position(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return new Coordinate2D(1, 1);
        }
        var numbers = value.EnumerateArray().ToArray();
        return numbers.Length >= 2
            ? new Coordinate2D(numbers[0].GetDouble(), numbers[1].GetDouble())
            : new Coordinate2D(1, 1);
    }

    private static List<Coordinate2D> PageCoordinates(JsonElement item)
    {
        if (!item.TryGetProperty("coordinates", out var coordinates)
            || coordinates.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Line graphic requires coordinates");
        }
        return coordinates.EnumerateArray()
            .Select(pair => pair.EnumerateArray().ToArray())
            .Where(pair => pair.Length >= 2)
            .Select(pair => new Coordinate2D(pair[0].GetDouble(), pair[1].GetDouble()))
            .ToList();
    }

    private static CIMLineSymbol LineSymbol(JsonElement config)
    {
        return SymbolFactory.Instance.ConstructLineSymbol(
            BridgeSupport.Color(config, "color", ColorFactory.Instance.CreateRGBColor(45, 45, 45)),
            BridgeSupport.Double(config, "width", 0.8),
            Enum.TryParse<SimpleLineStyle>(BridgeSupport.String(config, "line_style", "Solid"), true, out var style)
                ? style
                : SimpleLineStyle.Solid);
    }

    private static CIMPolygonSymbol PolygonSymbol(JsonElement config)
    {
        return SymbolFactory.Instance.ConstructPolygonSymbol(
            BridgeSupport.Color(config, "color", ColorFactory.Instance.CreateRGBColor(255, 255, 255, 80)),
            SimpleFillStyle.Solid,
            SymbolFactory.Instance.ConstructStroke(
                BridgeSupport.Color(config, "outline_color", ColorFactory.Instance.CreateRGBColor(45, 45, 45)),
                BridgeSupport.Double(config, "width", 0.6),
                SimpleLineStyle.Solid));
    }
}
