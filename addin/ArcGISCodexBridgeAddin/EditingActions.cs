using System.Text.Json;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class EditingActions
{
    internal static async Task<object> UpdateAttributesAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        RequireSourceEditConfirmation(payload);
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        if (!payload.TryGetProperty("updates", out var updates)
            || updates.ValueKind != JsonValueKind.Array
            || updates.GetArrayLength() == 0)
        {
            throw new ArgumentException("updates array is required");
        }
        var setup = await QueuedTask.Run(() =>
        {
            var layer = ResolveFeatureLayer(BridgeSupport.String(payload, "map_name"), layerName);
            if (!layer.CanEditData())
            {
                throw new InvalidOperationException($"Layer is not editable: {layerName}");
            }
            var operation = new EditOperation
            {
                Name = BridgeSupport.String(payload, "operation_name", $"Update {layerName} attributes")!,
                SelectModifiedFeatures = BridgeSupport.Bool(payload, "select_modified", false),
            };
            var ids = new List<long>();
            foreach (var update in updates.EnumerateArray())
            {
                var oid = update.GetProperty("object_id").GetInt64();
                var attributes = update.TryGetProperty("attributes", out var values)
                    ? Attributes(values)
                    : throw new ArgumentException("Each update requires attributes");
                operation.Modify(layer, oid, attributes);
                ids.Add(oid);
            }
            return (operation, layer.Map.Name, ids.ToArray());
        });
        var success = await setup.operation.ExecuteAsync();
        return new
        {
            map = setup.Name,
            layer = layerName,
            object_ids = setup.Item3,
            success,
            error = success ? null : setup.operation.ErrorMessage,
            source_data_edits_staged = success,
            edits_saved = false,
        };
    }

    internal static async Task<object> CreateFeaturesAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        RequireSourceEditConfirmation(payload);
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        if (!payload.TryGetProperty("features", out var features)
            || features.ValueKind != JsonValueKind.Array
            || features.GetArrayLength() == 0)
        {
            throw new ArgumentException("features array is required");
        }
        var setup = await QueuedTask.Run(() =>
        {
            var layer = ResolveFeatureLayer(BridgeSupport.String(payload, "map_name"), layerName);
            if (!layer.CanEditData())
            {
                throw new InvalidOperationException($"Layer is not editable: {layerName}");
            }
            var operation = new EditOperation
            {
                Name = BridgeSupport.String(payload, "operation_name", $"Create {layerName} features")!,
                SelectNewFeatures = BridgeSupport.Bool(payload, "select_new", false),
            };
            var count = 0;
            foreach (var feature in features.EnumerateArray())
            {
                var attributes = feature.TryGetProperty("attributes", out var values)
                    ? Attributes(values)
                    : new Dictionary<string, object?>();
                var geometry = Geometry(feature, layer.GetSpatialReference());
                operation.Create(layer, geometry, attributes);
                count++;
            }
            return (operation, layer.Map.Name, count);
        });
        var success = await setup.operation.ExecuteAsync();
        return new
        {
            map = setup.Name,
            layer = layerName,
            requested_count = setup.count,
            success,
            error = success ? null : setup.operation.ErrorMessage,
            source_data_edits_staged = success,
            edits_saved = false,
        };
    }

    internal static async Task<object> DeleteFeaturesAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        RequireSourceEditConfirmation(payload);
        if (!BridgeSupport.Bool(payload, "confirm_delete", false))
        {
            throw new ArgumentException("confirm_delete=true is required to delete source features");
        }
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var objectIds = Longs(payload, "object_ids");
        if (objectIds.Length == 0)
        {
            throw new ArgumentException("object_ids is required");
        }
        var setup = await QueuedTask.Run(() =>
        {
            var layer = ResolveFeatureLayer(BridgeSupport.String(payload, "map_name"), layerName);
            if (!layer.CanEditData())
            {
                throw new InvalidOperationException($"Layer is not editable: {layerName}");
            }
            var operation = new EditOperation
            {
                Name = BridgeSupport.String(payload, "operation_name", $"Delete {layerName} features")!,
            };
            operation.Delete(layer, objectIds);
            return (operation, layer.Map.Name);
        });
        var success = await setup.operation.ExecuteAsync();
        return new
        {
            map = setup.Name,
            layer = layerName,
            object_ids = objectIds,
            success,
            error = success ? null : setup.operation.ErrorMessage,
            source_data_deletes_staged = success,
            edits_saved = false,
        };
    }

    internal static async Task<object> SaveEditsAsync(JsonElement payload)
    {
        RequireSourceEditConfirmation(payload);
        var success = await Project.Current.SaveEditsAsync();
        return new
        {
            success,
            source_data_edits_saved = success,
            remaining_edits = Project.Current.HasEdits,
        };
    }

    internal static async Task<object> DiscardEditsAsync(JsonElement payload)
    {
        if (!BridgeSupport.Bool(payload, "confirm_discard", false))
        {
            throw new ArgumentException("confirm_discard=true is required to discard edits");
        }
        var success = await Project.Current.DiscardEditsAsync();
        return new
        {
            success,
            edits_discarded = success,
            remaining_edits = Project.Current.HasEdits,
        };
    }

    internal static async Task<object> UndoAsync(JsonElement payload)
    {
        RequireSourceEditConfirmation(payload);
        var success = await Project.Current.UndoEditAsync();
        return new { success, operation = "undo_edit", remaining_edits = Project.Current.HasEdits };
    }

    internal static async Task<object> RedoAsync(JsonElement payload)
    {
        RequireSourceEditConfirmation(payload);
        var success = await Project.Current.RedoEditAsync();
        return new { success, operation = "redo_edit", remaining_edits = Project.Current.HasEdits };
    }

    private static void RequireSourceEditConfirmation(JsonElement payload)
    {
        if (!BridgeSupport.Bool(payload, "confirm_source_edit", false))
        {
            throw new ArgumentException("confirm_source_edit=true is required for source editing actions");
        }
    }

    private static FeatureLayer ResolveFeatureLayer(string? requestedMap, string layerName)
    {
        var map = BridgeSupport.ResolveMap(requestedMap);
        return BridgeSupport.ResolveLayer(map, layerName) as FeatureLayer
            ?? throw new InvalidOperationException($"Feature layer not found: {layerName}");
    }

    private static Dictionary<string, object?> Attributes(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("attributes must be an object");
        }
        return value.EnumerateObject().ToDictionary(
            property => property.Name,
            property => Scalar(property.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    private static object? Scalar(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => value.GetDouble(),
            _ => throw new ArgumentException("Edit attribute values must be scalar JSON values"),
        };
    }

    private static Geometry Geometry(JsonElement feature, SpatialReference spatialReference)
    {
        if (!feature.TryGetProperty("geometry", out var geometry)
            || geometry.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Each feature requires a geometry object");
        }
        var type = BridgeSupport.String(geometry, "type")?.ToLowerInvariant()
            ?? throw new ArgumentException("geometry.type is required");
        if (type == "point")
        {
            var coordinates = Coordinates(geometry);
            if (coordinates.Count != 1)
            {
                throw new ArgumentException("Point geometry requires coordinates [x, y]");
            }
            return MapPointBuilderEx.CreateMapPoint(
                coordinates[0].X, coordinates[0].Y, spatialReference);
        }
        if (type is "line" or "polyline")
        {
            return PolylineBuilderEx.CreatePolyline(Coordinates(geometry), spatialReference);
        }
        if (type == "polygon")
        {
            return PolygonBuilderEx.CreatePolygon(Coordinates(geometry), spatialReference);
        }
        throw new ArgumentException($"Unsupported edit geometry type: {type}");
    }

    private static List<Coordinate2D> Coordinates(JsonElement geometry)
    {
        if (!geometry.TryGetProperty("coordinates", out var coordinates)
            || coordinates.ValueKind != JsonValueKind.Array)
        {
            return new List<Coordinate2D>();
        }
        var raw = coordinates.EnumerateArray().ToArray();
        if (raw.Length >= 2 && raw[0].ValueKind == JsonValueKind.Number)
        {
            return new List<Coordinate2D> { new(raw[0].GetDouble(), raw[1].GetDouble()) };
        }
        return raw
            .Where(item => item.ValueKind == JsonValueKind.Array)
            .Select(item => item.EnumerateArray().ToArray())
            .Where(pair => pair.Length >= 2)
            .Select(pair => new Coordinate2D(pair[0].GetDouble(), pair[1].GetDouble()))
            .ToList();
    }

    private static long[] Longs(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<long>();
        }
        return values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.Number)
            .Select(value => value.GetInt64())
            .Distinct()
            .ToArray();
    }
}
