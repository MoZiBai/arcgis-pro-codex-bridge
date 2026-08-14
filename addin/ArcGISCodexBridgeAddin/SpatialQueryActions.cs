using System.Globalization;
using System.Text.Json;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class SpatialQueryActions
{
    internal static async Task<object> QueryAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var targetName = BridgeSupport.String(payload, "target_layer_name")
            ?? throw new ArgumentException("target_layer_name is required");
        var maximum = Math.Clamp(BridgeSupport.Int(payload, "max_rows", 100), 1, 5000);
        var requestedFields = BridgeSupport.Strings(payload, "fields");
        var includeGeometry = BridgeSupport.Bool(payload, "include_geometry", false);
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(BridgeSupport.String(payload, "map_name"));
            var target = ResolveFeatureLayer(map, targetName);
            using var table = target.GetTable();
            using var definition = table.GetDefinition();
            var availableFields = definition.GetFields().Select(field => field.Name).ToArray();
            var fields = requestedFields.Length == 0
                ? availableFields
                : requestedFields.Where(field => availableFields.Contains(
                    field, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (fields.Length == 0)
            {
                throw new ArgumentException("No requested fields exist in the target layer schema.");
            }
            var internalFields = fields.ToList();
            if (definition.HasObjectID())
            {
                internalFields.Add(definition.GetObjectIDField());
            }
            if (includeGeometry && definition is FeatureClassDefinition featureDefinition)
            {
                internalFields.Add(featureDefinition.GetShapeField());
            }
            var queryFields = internalFields
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var filter = BuildSpatialFilter(payload, map, target, queryFields, maximum);
            var countFilter = BuildSpatialFilter(payload, map, target, new[] { "*" }, null);
            var total = table.GetCount(countFilter);
            var rows = new List<Dictionary<string, object?>>();
            using var cursor = target.Search(filter);
            while (rows.Count < maximum && cursor.MoveNext())
            {
                using var row = cursor.Current;
                var values = fields.ToDictionary(
                    field => field,
                    field => JsonValue(row[field]),
                    StringComparer.OrdinalIgnoreCase);
                values["__object_id"] = row.GetObjectID();
                if (includeGeometry && row is Feature feature)
                {
                    values["__geometry"] = GeometrySummary(feature.GetShape());
                }
                rows.Add(values);
            }
            return (object)new
            {
                map = map.Name,
                target_layer = target.Name,
                spatial_relationship = filter.SpatialRelationship.ToString(),
                where_clause = filter.WhereClause,
                total_matching = total,
                returned = rows.Count,
                truncated = total > rows.Count,
                fields,
                filter_geometry = GeometrySummary(filter.FilterGeometry),
                rows,
            };
        });
    }

    internal static async Task<object> SelectAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var targetName = BridgeSupport.String(payload, "target_layer_name")
            ?? throw new ArgumentException("target_layer_name is required");
        var combinationText = BridgeSupport.String(payload, "combination", "New")!;
        if (!Enum.TryParse<SelectionCombinationMethod>(combinationText, true, out var combination))
        {
            throw new ArgumentException($"Unsupported selection combination: {combinationText}");
        }
        var maximumIds = Math.Clamp(BridgeSupport.Int(payload, "max_ids", 1000), 0, 10000);
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(BridgeSupport.String(payload, "map_name"));
            var target = ResolveFeatureLayer(map, targetName);
            var filter = BuildSpatialFilter(payload, map, target, new[] { "*" }, 0);
            using var selection = target.Select(filter, combination);
            var ids = selection.GetObjectIDs();
            return (object)new
            {
                map = map.Name,
                target_layer = target.Name,
                spatial_relationship = filter.SpatialRelationship.ToString(),
                combination = combination.ToString(),
                selected_count = ids.Count,
                object_ids = ids.Take(maximumIds).ToArray(),
                object_ids_truncated = ids.Count > maximumIds,
                filter_geometry = GeometrySummary(filter.FilterGeometry),
            };
        });
    }

    private static SpatialQueryFilter BuildSpatialFilter(
        JsonElement payload,
        Map map,
        FeatureLayer target,
        IReadOnlyList<string> fields,
        int? rowCount)
    {
        var geometry = ResolveFilterGeometry(payload, map);
        var targetSpatialReference = target.GetSpatialReference();
        if (geometry.SpatialReference is not null
            && targetSpatialReference is not null
            && !geometry.SpatialReference.IsEqual(targetSpatialReference))
        {
            geometry = GeometryEngine.Instance.Project(geometry, targetSpatialReference);
        }
        var relationshipText = BridgeSupport.String(payload, "spatial_relationship", "Intersects")!;
        if (!Enum.TryParse<SpatialRelationship>(relationshipText, true, out var relationship))
        {
            throw new ArgumentException($"Unsupported spatial relationship: {relationshipText}");
        }
        var searchOrderText = BridgeSupport.String(payload, "search_order", "Spatial")!;
        if (!Enum.TryParse<SearchOrder>(searchOrderText, true, out var searchOrder))
        {
            throw new ArgumentException($"Unsupported search order: {searchOrderText}");
        }
        var filter = new SpatialQueryFilter
        {
            FilterGeometry = geometry,
            SpatialRelationship = relationship,
            SpatialRelationshipDescription = BridgeSupport.String(
                payload, "spatial_relationship_description", string.Empty),
            SearchOrder = searchOrder,
            WhereClause = BridgeSupport.String(payload, "where_clause", "1=1"),
            SubFields = fields.Count == 1 && fields[0] == "*" ? "*" : string.Join(",", fields),
        };
        if (rowCount is > 0)
        {
            filter.RowCount = rowCount.Value;
        }
        return filter;
    }

    private static Geometry ResolveFilterGeometry(JsonElement payload, Map map)
    {
        if (payload.TryGetProperty("geometry", out var geometry)
            && geometry.ValueKind == JsonValueKind.Object)
        {
            return ParseGeometry(geometry, map.SpatialReference);
        }
        var sourceLayerName = BridgeSupport.String(payload, "source_layer_name")
            ?? throw new ArgumentException("geometry or source_layer_name is required");
        var source = ResolveFeatureLayer(map, sourceLayerName);
        var sourceIds = Longs(payload, "source_object_ids");
        var filter = new QueryFilter
        {
            WhereClause = BridgeSupport.String(payload, "source_where_clause", "1=1"),
            ObjectIDs = sourceIds,
            SubFields = "*",
            RowCount = Math.Clamp(BridgeSupport.Int(payload, "max_source_features", 10000), 1, 100000),
        };
        var shapes = new List<Geometry>();
        using var cursor = source.Search(filter);
        while (cursor.MoveNext())
        {
            using var row = cursor.Current;
            if (row is Feature feature)
            {
                var shape = feature.GetShape();
                if (shape is not null && !shape.IsEmpty)
                {
                    shapes.Add(shape);
                }
            }
        }
        if (shapes.Count == 0)
        {
            throw new InvalidOperationException($"Source layer query returned no geometry: {sourceLayerName}");
        }
        return shapes.Count == 1 ? shapes[0] : GeometryEngine.Instance.Union(shapes);
    }

    private static Geometry ParseGeometry(JsonElement geometry, SpatialReference spatialReference)
    {
        var type = BridgeSupport.String(geometry, "type")?.ToLowerInvariant()
            ?? throw new ArgumentException("geometry.type is required");
        if (type == "envelope")
        {
            var bounds = geometry.TryGetProperty("bounds", out var value) ? value : geometry;
            return EnvelopeBuilderEx.CreateEnvelope(
                BridgeSupport.Double(bounds, "xmin"),
                BridgeSupport.Double(bounds, "ymin"),
                BridgeSupport.Double(bounds, "xmax"),
                BridgeSupport.Double(bounds, "ymax"),
                spatialReference);
        }
        var coordinates = Coordinates(geometry);
        return type switch
        {
            "point" when coordinates.Count >= 1 => MapPointBuilderEx.CreateMapPoint(
                coordinates[0].X, coordinates[0].Y, spatialReference),
            "polyline" or "line" when coordinates.Count >= 2 => PolylineBuilderEx.CreatePolyline(
                coordinates, spatialReference),
            "polygon" when coordinates.Count >= 3 => PolygonBuilderEx.CreatePolygon(
                coordinates, spatialReference),
            _ => throw new ArgumentException($"Invalid or unsupported filter geometry type: {type}"),
        };
    }

    private static List<Coordinate2D> Coordinates(JsonElement geometry)
    {
        if (!geometry.TryGetProperty("coordinates", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return new List<Coordinate2D>();
        }
        var raw = values.EnumerateArray().ToArray();
        if (raw.Length >= 2 && raw[0].ValueKind == JsonValueKind.Number)
        {
            return new List<Coordinate2D> { new(raw[0].GetDouble(), raw[1].GetDouble()) };
        }
        return raw
            .Where(value => value.ValueKind == JsonValueKind.Array)
            .Select(value => value.EnumerateArray().ToArray())
            .Where(pair => pair.Length >= 2)
            .Select(pair => new Coordinate2D(pair[0].GetDouble(), pair[1].GetDouble()))
            .ToList();
    }

    private static FeatureLayer ResolveFeatureLayer(Map map, string name)
    {
        return map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
            .FirstOrDefault(layer => string.Equals(
                layer.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Feature layer not found: {name}");
    }

    private static long[] Longs(JsonElement payload, string name)
    {
        return payload.TryGetProperty(name, out var values)
            && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.Number)
                .Select(value => value.GetInt64())
                .Distinct()
                .ToArray()
            : Array.Empty<long>();
    }

    private static object? JsonValue(object? value)
    {
        return value switch
        {
            null or DBNull => null,
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            Guid guid => guid.ToString(),
            byte[] bytes => Convert.ToBase64String(bytes),
            string or bool or byte or short or int or long or float or double or decimal => value,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }

    private static object? GeometrySummary(Geometry? geometry)
    {
        if (geometry is null || geometry.IsEmpty)
        {
            return null;
        }
        var extent = geometry.Extent;
        return new
        {
            geometry_type = geometry.GeometryType.ToString(),
            spatial_reference = geometry.SpatialReference?.Name,
            extent = new
            {
                xmin = extent.XMin,
                ymin = extent.YMin,
                xmax = extent.XMax,
                ymax = extent.YMax,
            },
        };
    }
}
