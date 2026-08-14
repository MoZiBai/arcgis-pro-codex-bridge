using System.IO;
using System.Text.Json;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class BookmarkActions
{
    internal static async Task<object> ConfigureAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var bookmarkName = BridgeSupport.String(payload, "bookmark_name")
            ?? throw new ArgumentException("bookmark_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(BridgeSupport.String(payload, "map_name"));
            var bookmark = ResolveBookmark(map, bookmarkName);
            var newName = BridgeSupport.String(payload, "new_name");
            if (!string.IsNullOrWhiteSpace(newName))
            {
                if (map.GetBookmarks().Any(item => !ReferenceEquals(item, bookmark) && string.Equals(
                    item.Name, newName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"Bookmark already exists: {newName}");
                }
                bookmark.Rename(newName);
            }
            if (payload.TryGetProperty("description", out _))
                bookmark.SetDescription(BridgeSupport.String(payload, "description", string.Empty));
            var definition = bookmark.GetDefinition();
            if (payload.TryGetProperty("folder_name", out _))
                definition.GroupName = BridgeSupport.String(payload, "folder_name", string.Empty);
            if (BridgeSupport.Bool(payload, "use_active_view_extent", false))
            {
                var view = MapView.Active;
                if (view?.Map != map)
                {
                    throw new InvalidOperationException("The bookmark map is not the active map view.");
                }
                definition.Location = view.Extent;
            }
            bookmark.SetDefinition(definition);
            return (object)new
            {
                map = map.Name,
                old_name = bookmarkName,
                bookmark = bookmark.Name,
                description = bookmark.Description,
                folder_name = bookmark.FolderName,
                extent_updated = BridgeSupport.Bool(payload, "use_active_view_extent", false),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> MoveAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var bookmarkName = BridgeSupport.String(payload, "bookmark_name")
            ?? throw new ArgumentException("bookmark_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(BridgeSupport.String(payload, "map_name"));
            var bookmark = ResolveBookmark(map, bookmarkName);
            var count = map.GetBookmarks().Count;
            var index = Math.Clamp(BridgeSupport.Int(payload, "index"), 0, Math.Max(0, count - 1));
            map.MoveBookmark(bookmark, index);
            return (object)new
            {
                map = map.Name,
                bookmark = bookmark.Name,
                index,
                order = map.GetBookmarks().Select(item => item.Name).ToArray(),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> ImportAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var input = BridgeSupport.String(payload, "input_file")
            ?? throw new ArgumentException("input_file is required");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(BridgeSupport.String(payload, "map_name"));
            var imported = map.ImportBookmarks(input);
            return (object)new
            {
                map = map.Name,
                input,
                imported = imported.Select(item => item.Name).ToArray(),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> ExportAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var output = BridgeSupport.String(payload, "output_file")
            ?? throw new ArgumentException("output_file is required");
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? string.Empty);
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(BridgeSupport.String(payload, "map_name"));
            map.ExportBookmarks(output);
            return (object)new
            {
                map = map.Name,
                output,
                count = map.GetBookmarks().Count,
                derived_output_created = true,
                source_data_modified = false,
            };
        });
    }

    private static Bookmark ResolveBookmark(Map map, string name)
    {
        return map.GetBookmarks().FirstOrDefault(item => string.Equals(
            item.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Bookmark not found: {name}");
    }
}
