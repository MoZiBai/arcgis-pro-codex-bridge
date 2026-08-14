using System.IO;
using System.Text.Json;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;

namespace ArcGISCodexBridgeAddin;

internal static class CatalogActions
{
    internal static async Task<object> ListAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var maximum = Math.Clamp(BridgeSupport.Int(payload, "max_items", 1000), 1, 5000);
        return await QueuedTask.Run<object>(() =>
        {
            var project = Project.Current
                ?? throw new InvalidOperationException("No ArcGIS Pro project is loaded.");
            var items = project.Items.Take(maximum).Select(ItemInfo).ToArray();
            var favorites = FavoritesManager.Current.GetFavorites()
                .Take(maximum)
                .Select(favorite => new
                {
                    item = ItemInfo(favorite.Item),
                    add_to_all_new_projects = favorite.IsAddedToAllNewProjects,
                }).ToArray();
            return (object)new
            {
                home_folder = project.HomeFolderPath,
                default_geodatabase = project.DefaultGeodatabasePath,
                default_toolbox = project.DefaultToolboxPath,
                total_project_items = project.Items.Count,
                returned_project_items = items.Length,
                project_items_truncated = project.Items.Count > items.Length,
                items,
                favorites,
            };
        });
    }

    internal static async Task<object> AddProjectItemAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var path = BridgeSupport.String(payload, "path")
            ?? throw new ArgumentException("path is required");
        var alias = BridgeSupport.String(payload, "alias");
        return await QueuedTask.Run<object>(() =>
        {
            var project = Project.Current
                ?? throw new InvalidOperationException("No ArcGIS Pro project is loaded.");
            var existing = project.Items.FirstOrDefault(item => SamePath(item.Path, path));
            if (existing is not null)
            {
                return (object)new
                {
                    added = false,
                    already_present = true,
                    item = ItemInfo(existing),
                    source_datasets_modified = false,
                };
            }
            var item = ItemFactory.Instance.Create(path, ItemFactory.ItemType.PathItem);
            if (item is not IProjectItem projectItem)
            {
                throw new InvalidOperationException($"The path is not a supported ArcGIS project item: {path}");
            }
            if (!string.IsNullOrWhiteSpace(alias))
            {
                item.SetAlias(alias);
            }
            var added = project.AddItem(projectItem);
            return (object)new
            {
                added,
                already_present = false,
                item = ItemInfo(item),
                source_datasets_modified = false,
            };
        });
    }

    internal static async Task<object> RemoveProjectItemAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!BridgeSupport.Bool(payload, "confirm_remove", false))
        {
            throw new ArgumentException("confirm_remove=true is required to remove a project item reference");
        }
        return await QueuedTask.Run<object>(() =>
        {
            var project = Project.Current
                ?? throw new InvalidOperationException("No ArcGIS Pro project is loaded.");
            var item = ResolveItem(project, payload);
            if (item is not IProjectItem projectItem)
            {
                throw new InvalidOperationException($"Item cannot be removed from the project: {item.Name}");
            }
            var removed = project.RemoveItem(projectItem);
            return (object)new
            {
                item = ItemInfo(item),
                removed_from_project = removed,
                source_dataset_deleted = false,
            };
        });
    }

    internal static async Task<object> RepairProjectItemAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var newPath = BridgeSupport.String(payload, "new_path")
            ?? throw new ArgumentException("new_path is required");
        if (!BridgeSupport.Bool(payload, "confirm_repair", false))
        {
            throw new ArgumentException("confirm_repair=true is required to change a project item reference");
        }
        return await QueuedTask.Run<object>(() =>
        {
            var project = Project.Current
                ?? throw new InvalidOperationException("No ArcGIS Pro project is loaded.");
            var item = ResolveItem(project, payload);
            var oldPath = item.Path;
            var repaired = item.Repair(newPath);
            return (object)new
            {
                item = item.Name,
                old_path = oldPath,
                new_path = item.Path,
                repaired,
                source_datasets_modified = false,
            };
        });
    }

    internal static async Task<object> ManageFavoriteAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var operation = BridgeSupport.String(payload, "operation", "list")!.ToLowerInvariant();
        return await QueuedTask.Run<object>(() =>
        {
            var manager = FavoritesManager.Current;
            if (operation == "list")
            {
                return (object)new
                {
                    favorites = manager.GetFavorites().Select(favorite => new
                    {
                        item = ItemInfo(favorite.Item),
                        add_to_all_new_projects = favorite.IsAddedToAllNewProjects,
                    }).ToArray(),
                };
            }
            var path = BridgeSupport.String(payload, "path")
                ?? throw new ArgumentException("path is required for favorite mutation");
            var existing = manager.GetFavorites()
                .FirstOrDefault(favorite => SamePath(favorite.Item.Path, path));
            if (operation == "remove")
            {
                if (existing is not null)
                {
                    manager.RemoveFavorite(existing.Item);
                }
                return (object)new { operation, path, removed = existing is not null };
            }

            var item = existing?.Item
                ?? ItemFactory.Instance.Create(path, ItemFactory.ItemType.PathItem);
            if (!manager.CanAddAsFavorite(item) && existing is null)
            {
                throw new InvalidOperationException($"Item cannot be added as a favorite: {path}");
            }
            var addToAll = BridgeSupport.Bool(payload, "add_to_all_new_projects", false);
            var favorite = existing ?? manager.AddFavorite(item, addToAll);
            if (addToAll)
            {
                manager.SetIsAddedToAllNewProjects(favorite.Item);
            }
            else
            {
                manager.ClearIsAddedToAllNewProjects(favorite.Item);
            }
            return (object)new
            {
                operation = existing is null ? "add" : "update",
                item = ItemInfo(favorite.Item),
                add_to_all_new_projects = addToAll,
            };
        });
    }

    private static Item ResolveItem(Project project, JsonElement payload)
    {
        var path = BridgeSupport.String(payload, "path");
        var name = BridgeSupport.String(payload, "name");
        if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("path or name is required");
        }
        return project.Items.FirstOrDefault(item =>
                (!string.IsNullOrWhiteSpace(path) && SamePath(item.Path, path))
                || (!string.IsNullOrWhiteSpace(name)
                    && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException($"Project item not found: {path ?? name}");
    }

    private static object ItemInfo(Item item) => new
    {
        name = item.Name,
        path = item.Path,
        type = item.Type,
        type_id = item.TypeID,
        is_container = item.IsContainer,
        is_invalid = item.IsInvalid,
        is_default = item.IsDefault,
        alias = item.Alias,
    };

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }
}
