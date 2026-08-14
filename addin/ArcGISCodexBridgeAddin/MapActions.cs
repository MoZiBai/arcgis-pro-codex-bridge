using System.Text.Json;
using System.IO;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class MapActions
{
    internal static async Task<object> CreateAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var mapName = BridgeSupport.String(payload, "map_name")
            ?? throw new ArgumentException("map_name is required");
        var openMap = BridgeSupport.Bool(payload, "open_map", false);
        Map? createdMap = null;

        var result = await QueuedTask.Run<object>(() =>
        {
            if (Project.Current is null)
            {
                throw new InvalidOperationException("No ArcGIS Pro project is loaded.");
            }
            if (Project.Current.GetItems<MapProjectItem>()
                .Any(item => string.Equals(item.Name, mapName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Map already exists: {mapName}");
            }

            var mapType = Enum.TryParse<MapType>(
                BridgeSupport.String(payload, "map_type", "Map"), true, out var parsedMapType)
                ? parsedMapType
                : throw new ArgumentException("Unsupported map_type");
            var defaultViewingMode = mapType == MapType.Scene
                ? MapViewingMode.SceneLocal
                : MapViewingMode.Map;
            var viewingMode = Enum.TryParse<MapViewingMode>(
                BridgeSupport.String(payload, "viewing_mode"), true, out var parsedViewingMode)
                ? parsedViewingMode
                : defaultViewingMode;
            var basemap = Enum.TryParse<Basemap>(
                BridgeSupport.String(payload, "basemap", "None"), true, out var parsedBasemap)
                ? parsedBasemap
                : throw new ArgumentException("Unsupported basemap");
            if (mapType == MapType.Map && viewingMode is MapViewingMode.SceneLocal or MapViewingMode.SceneGlobal)
            {
                throw new ArgumentException("A 2D map cannot use a scene viewing mode");
            }
            if (mapType == MapType.Scene && viewingMode == MapViewingMode.Map)
            {
                throw new ArgumentException("A scene requires SceneLocal or SceneGlobal viewing_mode");
            }
            createdMap = MapFactory.Instance.CreateMap(mapName, mapType, viewingMode, basemap);
            return (object)new
            {
                map = createdMap.Name,
                map_type = createdMap.MapType.ToString(),
                viewing_mode = createdMap.DefaultViewingMode.ToString(),
                basemap = basemap.ToString(),
            };
        });

        if (openMap && createdMap is not null)
        {
            await await FrameworkApplication.Current.Dispatcher.InvokeAsync(
                () => FrameworkApplication.Panes.CreateMapPaneAsync(createdMap));
        }
        return result;
    }

    internal static async Task<object> RenameAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var mapName = BridgeSupport.String(payload, "map_name")
            ?? throw new ArgumentException("map_name is required");
        var newName = BridgeSupport.String(payload, "new_name")
            ?? throw new ArgumentException("new_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            if (Project.Current.GetItems<MapProjectItem>()
                .Any(item => string.Equals(item.Name, newName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Map already exists: {newName}");
            }
            var map = BridgeSupport.ResolveMap(mapName);
            map.SetName(newName);
            return (object)new { old_name = mapName, map = map.Name };
        });
    }

    internal static async Task<object> DuplicateAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var mapName = BridgeSupport.String(payload, "map_name")
            ?? throw new ArgumentException("map_name is required");
        var newName = BridgeSupport.String(payload, "new_name")
            ?? throw new ArgumentException("new_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            if (Project.Current.GetItems<MapProjectItem>()
                .Any(item => string.Equals(item.Name, newName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Map already exists: {newName}");
            }
            var source = BridgeSupport.ResolveMap(mapName);
            var copy = MapFactory.Instance.CopyMap(source);
            copy.SetName(newName);
            return (object)new
            {
                source_map = source.Name,
                map = copy.Name,
                source_datasets_copied = false,
            };
        });
    }

    internal static async Task<object> RemoveAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var mapName = BridgeSupport.String(payload, "map_name")
            ?? throw new ArgumentException("map_name is required");
        if (!BridgeSupport.Bool(payload, "confirm_remove", false))
        {
            throw new ArgumentException("confirm_remove=true is required to remove a map project item");
        }
        return await QueuedTask.Run<object>(() =>
        {
            var item = Project.Current.GetItems<MapProjectItem>()
                .FirstOrDefault(candidate => string.Equals(
                    candidate.Name, mapName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Map not found: {mapName}");
            var removed = Project.Current.RemoveItem(item);
            return (object)new
            {
                map = mapName,
                removed_from_project = removed,
                source_datasets_deleted = false,
            };
        });
    }

    internal static async Task<object> OpenAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var mapName = BridgeSupport.String(payload, "map_name")
            ?? throw new ArgumentException("map_name is required");
        var map = await QueuedTask.Run(() => BridgeSupport.ResolveMap(mapName));
        await await FrameworkApplication.Current.Dispatcher.InvokeAsync(
            () => FrameworkApplication.Panes.CreateMapPaneAsync(map));
        return new { map = map.Name, opened = true };
    }

    internal static async Task<object> ListBookmarksAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            return (object)new
            {
                map = map.Name,
                bookmarks = map.GetBookmarks().Select((bookmark, index) => new
                {
                    index,
                    name = bookmark.Name,
                    description = bookmark.Description,
                    folder_name = bookmark.FolderName,
                    has_time_extent = bookmark.HasTimeExtent,
                    has_range_extent = bookmark.HasRangeExtent,
                }).ToArray(),
            };
        });
    }

    internal static async Task<object> CreateBookmarkAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var name = BridgeSupport.String(payload, "bookmark_name")
            ?? throw new ArgumentException("bookmark_name is required");
        var description = BridgeSupport.String(payload, "description", string.Empty)!;
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var view = MapView.Active;
            if (view is null || view.Map != map)
            {
                throw new InvalidOperationException("The requested map must be the active map view to create a bookmark.");
            }
            if (map.GetBookmarks().Any(bookmark => string.Equals(
                bookmark.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Bookmark already exists: {name}");
            }
            var bookmark = map.AddBookmark(view, name, description);
            return (object)new { map = map.Name, bookmark = bookmark.Name, description };
        });
    }

    internal static async Task<object> RemoveBookmarkAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var name = BridgeSupport.String(payload, "bookmark_name")
            ?? throw new ArgumentException("bookmark_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var bookmark = map.GetBookmarks().FirstOrDefault(item => string.Equals(
                item.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Bookmark not found: {name}");
            map.RemoveBookmark(bookmark);
            return (object)new { map = map.Name, bookmark = name, removed = true };
        });
    }

    internal static async Task<object> ZoomBookmarkAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var name = BridgeSupport.String(payload, "bookmark_name")
            ?? throw new ArgumentException("bookmark_name is required");
        var resolved = await QueuedTask.Run(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var bookmark = map.GetBookmarks().FirstOrDefault(item => string.Equals(
                item.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Bookmark not found: {name}");
            return (map, bookmark);
        });
        var view = MapView.Active;
        if (view is null || view.Map != resolved.map)
        {
            throw new InvalidOperationException("The requested map must be the active map view to zoom to a bookmark.");
        }
        var success = await view.ZoomToAsync(resolved.bookmark, TimeSpan.Zero);
        return new { map = resolved.map.Name, bookmark = name, success };
    }

    internal static Task<object> GetCameraAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        CameraTrace("start");
        var result = FrameworkApplication.Current.Dispatcher.Invoke<object>(() =>
        {
            CameraTrace("ui-enter");
            var view = MapView.Active;
            if (view is null)
            {
                CameraTrace("ui-no-active-view");
                throw new InvalidOperationException("There is no active map view to inspect its camera.");
            }
            CameraTrace("ui-view-captured");
            var mapName = view.Map.Name;
            CameraTrace("ui-map-name");
            if (!string.IsNullOrWhiteSpace(requestedMap)
                && !string.Equals(mapName, requestedMap, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The requested map must be the active map view to inspect its camera.");
            }
            var camera = view.Camera;
            CameraTrace("ui-camera");
            var spatialReference = camera.SpatialReference?.Name;
            CameraTrace("ui-spatial-reference");
            var x = Finite(camera.X);
            CameraTrace("ui-x");
            var y = Finite(camera.Y);
            CameraTrace("ui-y");
            var z = Finite(camera.Z);
            CameraTrace("ui-z");
            var scale = Finite(camera.Scale);
            CameraTrace("ui-scale");
            var heading = Finite(camera.Heading);
            CameraTrace("ui-heading");
            var pitch = Finite(camera.Pitch);
            CameraTrace("ui-pitch");
            var roll = Finite(camera.Roll);
            CameraTrace("ui-roll");
            object result = new
            {
                map = mapName,
                x,
                y,
                z,
                scale,
                heading,
                pitch,
                roll,
                spatial_reference = spatialReference,
            };
            CameraTrace("ui-result-ready");
            return result;
        });
        CameraTrace("dispatcher-returned");
        return Task.FromResult(result);
    }

    private static void CameraTrace(string stage)
    {
        try
        {
            var queueRoot = Environment.GetEnvironmentVariable("ARCGIS_CODEX_BRIDGE_QUEUE")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ArcGISCodexBridge",
                    "queue");
            Directory.CreateDirectory(queueRoot);
            File.AppendAllText(
                Path.Combine(queueRoot, "camera_trace.log"),
                $"{DateTime.UtcNow:O}\t{stage}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never change bridge behavior.
        }
    }

    private static double? Finite(double value)
    {
        return double.IsFinite(value) ? value : null;
    }

    internal static async Task<object> SetCameraAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var view = MapView.Active;
            if (view is null || view.Map != map)
            {
                throw new InvalidOperationException("The requested map must be the active map view to change its camera.");
            }
            var camera = view.Camera;
            if (payload.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.Number)
            {
                camera.X = x.GetDouble();
            }
            if (payload.TryGetProperty("y", out var y) && y.ValueKind == JsonValueKind.Number)
            {
                camera.Y = y.GetDouble();
            }
            if (payload.TryGetProperty("z", out var z) && z.ValueKind == JsonValueKind.Number)
            {
                camera.Z = z.GetDouble();
            }
            if (payload.TryGetProperty("scale", out var scale) && scale.ValueKind == JsonValueKind.Number)
            {
                camera.Scale = Math.Max(1, scale.GetDouble());
            }
            if (payload.TryGetProperty("heading", out var heading) && heading.ValueKind == JsonValueKind.Number)
            {
                camera.Heading = heading.GetDouble();
            }
            if (payload.TryGetProperty("pitch", out var pitch) && pitch.ValueKind == JsonValueKind.Number)
            {
                camera.Pitch = pitch.GetDouble();
            }
            if (payload.TryGetProperty("roll", out var roll) && roll.ValueKind == JsonValueKind.Number)
            {
                camera.Roll = roll.GetDouble();
            }
            var success = view.ZoomTo(camera, TimeSpan.Zero);
            return (object)new
            {
                map = map.Name,
                success,
                x = Finite(camera.X),
                y = Finite(camera.Y),
                z = Finite(camera.Z),
                scale = Finite(camera.Scale),
                heading = Finite(camera.Heading),
                pitch = Finite(camera.Pitch),
                roll = Finite(camera.Roll),
            };
        });
    }

    internal static async Task<object> ZoomFullExtentAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var map = await QueuedTask.Run(() => BridgeSupport.ResolveMap(requestedMap));
        var view = MapView.Active;
        if (view is null || view.Map != map)
        {
            throw new InvalidOperationException("The requested map must be the active map view to zoom to full extent.");
        }
        var success = await view.ZoomToFullExtentAsync(TimeSpan.Zero);
        return new { map = map.Name, success };
    }

    internal static async Task<object> GetCimAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var definition = map.GetDefinition();
            return (object)new
            {
                map = map.Name,
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
            throw new ArgumentException("confirm_replace_definition=true is required to replace a map CIM definition");
        }
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var cimJson = BridgeSupport.String(payload, "cim_json")
            ?? throw new ArgumentException("cim_json is required");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var definition = CIMMap.FromJson(cimJson, new JsonDeserializationSettings());
            map.SetDefinition(definition);
            return (object)new
            {
                map = map.Name,
                cim_type = definition.GetType().Name,
                definition_replaced = true,
                source_data_modified = false,
            };
        });
    }
}
