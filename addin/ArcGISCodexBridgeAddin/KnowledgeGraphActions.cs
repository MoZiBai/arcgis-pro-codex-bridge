using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Data.Knowledge;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.KnowledgeGraph;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class KnowledgeGraphActions
{
    internal static async Task<object> ListAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var includeModel = BridgeSupport.Bool(payload, "include_data_model", true);
        return await QueuedTask.Run<object>(() =>
        {
            var items = Project.Current.GetItems<KnowledgeGraphInvestigationProjectItem>();
            return (object)new
            {
                investigations = items.Select(item => DescribeInvestigation(item, includeModel)).ToArray(),
                count = items.Count(),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> ManageInvestigationAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var name = Required(payload, "investigation_name");
        var operation = Required(payload, "operation").ToLowerInvariant();
        return await QueuedTask.Run<object>(async () =>
        {
            var item = ResolveInvestigationItem(name);
            var investigation = item.GetInvestigation();
            if (operation == "open")
                await item.OpenInvestigationPaneAsync();
            else if (operation == "rename")
                investigation.SetName(Required(payload, "new_name"));
            else
                throw new ArgumentException("operation must be open or rename");
            return DescribeInvestigation(item, false);
        });
    }

    internal static async Task<object> QueryAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var investigationName = Required(payload, "investigation_name");
        var operation = BridgeSupport.String(payload, "operation", "query")!.ToLowerInvariant();
        var maxRows = Math.Clamp(BridgeSupport.Int(payload, "max_rows", 100), 1, 1000);
        var maxValueItems = Math.Clamp(BridgeSupport.Int(payload, "max_value_items", 100), 1, 1000);
        return await QueuedTask.Run<object>(() =>
        {
            var item = ResolveInvestigationItem(investigationName);
            using var graph = item.GetDatastore();
            KnowledgeGraphCursor cursor;
            if (operation == "query")
            {
                var queryText = Required(payload, "query_text");
                if (queryText.Length > 100_000)
                    throw new ArgumentException("query_text is limited to 100,000 characters");
                var filter = new KnowledgeGraphQueryFilter
                {
                    QueryText = queryText,
                    ProvenanceBehavior = ParseEnum(
                        BridgeSupport.String(payload, "provenance_behavior", "Exclude"),
                        KnowledgeGraphProvenanceBehavior.Exclude),
                };
                cursor = graph.SubmitQuery(filter);
            }
            else if (operation == "search")
            {
                var filter = new KnowledgeGraphSearchFilter
                {
                    SearchText = Required(payload, "search_text"),
                    MaxRowCount = maxRows,
                    Offset = Math.Max(0, BridgeSupport.Int(payload, "offset", 0)),
                    ReturnSearchContext = BridgeSupport.Bool(payload, "return_search_context", false),
                    SearchTarget = ParseEnum(
                        BridgeSupport.String(payload, "search_target", "BothEntityRelationship"),
                        KnowledgeGraphNamedTypeCategory.BothEntityRelationship),
                };
                cursor = graph.SubmitSearch(filter);
            }
            else
            {
                throw new ArgumentException("operation must be query or search");
            }
            using (cursor)
            {
                var keys = cursor.GetKeys().ToArray();
                var rows = new List<Dictionary<string, object?>>();
                while (rows.Count < maxRows && cursor.MoveNext())
                {
                    using var row = cursor.Current;
                    var materialized = new Dictionary<string, object?>(StringComparer.Ordinal);
                    for (var index = 0; index < Math.Min(keys.Length, (int)row.GetCount()); index++)
                        materialized[keys[index]] = MaterializeValue(row[index], 0, maxValueItems);
                    rows.Add(materialized);
                }
                var serverError = cursor.GetHasError() ? cursor.GetError() : null;
                return (object)new
                {
                    investigation = item.Name,
                    operation,
                    keys,
                    rows,
                    row_count = rows.Count,
                    truncated = rows.Count >= maxRows,
                    server_error = serverError is null ? null : new
                    {
                        code = serverError.ErrorCode,
                        message = serverError.ErrorMessage,
                    },
                    source_data_modified = false,
                };
            }
        });
    }

    internal static async Task<object> GetLinkChartStateAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(() => DescribeLinkChart(ResolveLinkChartView(requestedMap)));
    }

    internal static async Task<object> ConfigureLinkChartAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(async () =>
        {
            var view = ResolveLinkChartView(requestedMap);
            if (payload.TryGetProperty("layout", out _))
            {
                var raw = BridgeSupport.String(payload, "layout");
                if (!Enum.TryParse<KnowledgeLinkChartLayoutAlgorithm>(raw, true, out var layout))
                    throw new ArgumentException($"Unsupported link-chart layout: {raw}");
                await view.SetLinkChartLayoutAsync(
                    layout, BridgeSupport.Bool(payload, "apply_layout_to_selection", false));
            }
            if (payload.TryGetProperty("show_nonspatial_data", out _))
                view.SetShowNonSpatialData(BridgeSupport.Bool(payload, "show_nonspatial_data"));
            if (payload.TryGetProperty("show_root_nodes", out _))
                view.SetShowRootNodes(BridgeSupport.Bool(payload, "show_root_nodes"));
            if (BridgeSupport.Bool(payload, "clear_root_nodes"))
                view.ClearRootNodes();
            if (payload.TryGetProperty("root_nodes", out var rootNodes)
                && rootNodes.ValueKind == JsonValueKind.Object)
            {
                var members = new Dictionary<MapMember, List<long>>();
                foreach (var property in rootNodes.EnumerateObject())
                {
                    var member = view.Map.GetMapMembersAsFlattenedList().FirstOrDefault(item => string.Equals(
                        item.Name, property.Name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Map member not found: {property.Name}");
                    if (property.Value.ValueKind != JsonValueKind.Array)
                        throw new ArgumentException("Each root_nodes value must be an array of object IDs");
                    members[member] = property.Value.EnumerateArray().Select(item => item.GetInt64()).ToList();
                }
                view.SetRootNodes(MapMemberIDSet.FromDictionary(members));
            }
            return DescribeLinkChart(view);
        });
    }

    private static object DescribeInvestigation(
        KnowledgeGraphInvestigationProjectItem item,
        bool includeModel)
    {
        var investigation = item.GetInvestigation();
        object? dataModel = null;
        string? error = null;
        if (includeModel)
        {
            try
            {
                using var graph = item.GetDatastore();
                dataModel = DescribeDataModel(graph);
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }
        }
        return new
        {
            name = investigation.Name,
            project_item_name = item.Name,
            uri = investigation.URI,
            service_uri = investigation.ServiceUri,
            graph_name = investigation.GraphName,
            pane_count = investigation.GetInvestigationPanes().Count,
            data_connection_type = investigation.DataConnection?.GetType().Name,
            data_connection_cim_json = investigation.DataConnection?.ToJson(),
            data_model = dataModel,
            data_model_error = error,
            source_data_modified = false,
        };
    }

    private static object DescribeDataModel(KnowledgeGraph graph)
    {
        var model = graph.GetDataModel();
        return new
        {
            timestamp = model.GetTimestamp(),
            strict = model.GetIsStrict(),
            arcgis_managed = model.GetIsArcGISManaged(),
            oid_property_name = model.GetOIDPropertyName(),
            spatial_reference = model.GetSpatialReference()?.Name,
            supported_search_targets = graph.GetSupportedSearchTargets().Select(item => item.ToString()).ToArray(),
            property_names = graph.GetPropertyNameInfo(),
            entities = model.GetEntityTypes().Values.Select(DescribeNamedType).ToArray(),
            relationships = model.GetRelationshipTypes().Values.Select(type => new
            {
                named_type = DescribeNamedType(type),
                endpoints = type.GetEndPoints().Select(endpoint => new
                {
                    origin = endpoint.GetOriginEntityTypeName(),
                    destination = endpoint.GetDestinationEntityTypeName(),
                }).ToArray(),
            }).ToArray(),
        };
    }

    private static object DescribeNamedType(KnowledgeGraphNamedObjectType type)
    {
        return new
        {
            name = type.GetName(),
            alias = type.GetAliasName(),
            role = type.GetRole().ToString(),
            strict = type.GetIsStrict(),
            spatial = type.GetIsSpatial(),
            has_object_id = type.GetHasObjectID(),
            object_id_property = type.GetObjectIDPropertyName(),
            shape_field = type.GetIsSpatial() ? type.GetShapeField() : null,
            properties = type.GetProperties().Select(property => new
            {
                name = property.Name,
                alias = property.AliasName,
                field_type = property.FieldType.ToString(),
                role = property.GetRole().ToString(),
                nullable = property.IsNullable,
                editable = property.IsEditable,
                system_maintained = property.GetIsSystemMaintained(),
                default_visible = property.GetHasDefaultVisibility(),
            }).ToArray(),
        };
    }

    private static object DescribeLinkChart(MapView view)
    {
        var roots = view.GetRootNodes().ToDictionary().Select(pair => new
        {
            map_member = pair.Key.Name,
            object_ids = pair.Value,
        }).ToArray();
        return new
        {
            map = view.Map.Name,
            is_link_chart = view.Map.IsLinkChart,
            layout = view.GetLinkChartLayout().ToString(),
            show_nonspatial_data = view.GetShowNonSpatialData(),
            show_root_nodes = view.GetShowRootNodes(),
            root_nodes = roots,
            source_data_modified = false,
        };
    }

    private static object? MaterializeValue(object? value, int depth, int maxItems)
    {
        if (value is null)
            return null;
        if (depth >= 8)
            return new { truncated = true, type = value.GetType().Name };
        if (value is KnowledgeGraphPrimitiveValue primitive)
            return MaterializeValue(primitive.GetValue(), depth + 1, maxItems);
        if (value is KnowledgeGraphEntityValue entity)
            return DescribeNamedValue(entity, depth, maxItems, entity.GetLabel(), null, null);
        if (value is KnowledgeGraphRelationshipValue relationship)
            return DescribeNamedValue(
                relationship,
                depth,
                maxItems,
                null,
                relationship.GetHasRelatedEntityIDs() ? relationship.GetOriginID() : null,
                relationship.GetHasRelatedEntityIDs() ? relationship.GetDestinationID() : null);
        if (value is KnowledgeGraphArrayValue array)
        {
            var count = Math.Min((ulong)maxItems, array.GetSize());
            return new
            {
                value_type = "Array",
                count = array.GetSize(),
                items = Enumerable.Range(0, (int)count)
                    .Select(index => MaterializeValue(array[(ulong)index], depth + 1, maxItems)).ToArray(),
                truncated = array.GetSize() > count,
            };
        }
        if (value is KnowledgeGraphPathValue path)
        {
            var entities = Math.Min((ulong)maxItems, path.GetEntityCount());
            var relationships = Math.Min((ulong)maxItems, path.GetRelationshipCount());
            return new
            {
                value_type = "Path",
                entity_count = path.GetEntityCount(),
                relationship_count = path.GetRelationshipCount(),
                entities = Enumerable.Range(0, (int)entities)
                    .Select(index => MaterializeValue(path.GetEntity((ulong)index), depth + 1, maxItems)).ToArray(),
                relationships = Enumerable.Range(0, (int)relationships)
                    .Select(index => MaterializeValue(path.GetRelationship((ulong)index), depth + 1, maxItems)).ToArray(),
                truncated = path.GetEntityCount() > entities || path.GetRelationshipCount() > relationships,
            };
        }
        if (value is KnowledgeGraphObjectValue graphObject)
        {
            var keys = graphObject.GetKeys().Take(maxItems).ToArray();
            return new
            {
                value_type = graphObject.KnowledgeGraphValueType.ToString(),
                properties = keys.ToDictionary(
                    key => key,
                    key => MaterializeValue(graphObject[key], depth + 1, maxItems)),
                truncated = graphObject.GetKeys().Count > keys.Length,
            };
        }
        if (value is DateTime dateTime)
            return dateTime.ToString("O");
        if (value is DateTimeOffset offset)
            return offset.ToString("O");
        if (value is string or bool or byte or short or int or long or float or double or decimal or Guid)
            return value;
        return value.ToString();
    }

    private static object DescribeNamedValue(
        KnowledgeGraphNamedObjectValue value,
        int depth,
        int maxItems,
        string? label,
        object? origin,
        object? destination)
    {
        var keys = value.GetKeys().Take(maxItems).ToArray();
        return new
        {
            value_type = value.KnowledgeGraphValueType.ToString(),
            type_name = value.GetTypeName(),
            id = MaterializeValue(value.GetID(), depth + 1, maxItems),
            object_id = value.GetHasObjectID() ? value.GetObjectID() : (long?)null,
            label,
            origin_id = MaterializeValue(origin, depth + 1, maxItems),
            destination_id = MaterializeValue(destination, depth + 1, maxItems),
            properties = keys.ToDictionary(
                key => key,
                key => MaterializeValue(value[key], depth + 1, maxItems)),
            truncated = value.GetKeys().Count > keys.Length,
        };
    }

    private static T ParseEnum<T>(string? raw, T fallback) where T : struct, Enum
    {
        return Enum.TryParse<T>(raw, true, out var parsed) ? parsed : fallback;
    }

    private static KnowledgeGraphInvestigationProjectItem ResolveInvestigationItem(string name)
    {
        return Project.Current.GetItems<KnowledgeGraphInvestigationProjectItem>()
            .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.GetInvestigation().Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Knowledge Graph investigation not found: {name}");
    }

    private static MapView ResolveLinkChartView(string? requestedMap)
    {
        var view = MapView.Active
            ?? throw new InvalidOperationException("There is no active map view");
        if (!string.IsNullOrWhiteSpace(requestedMap)
            && !string.Equals(view.Map.Name, requestedMap, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The requested map must be the active map view");
        if (!view.Map.IsLinkChart)
            throw new InvalidOperationException("The active map view is not a Knowledge Graph link chart");
        return view;
    }

    private static string Required(JsonElement payload, string name)
    {
        return BridgeSupport.String(payload, name)
            ?? throw new ArgumentException($"{name} is required");
    }
}
