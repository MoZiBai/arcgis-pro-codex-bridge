using System.Globalization;
using System.Text.Json;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class QueryActions
{
    internal static async Task<object> SchemaAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(() =>
        {
            var layer = ResolveFeatureLayer(requestedMap, layerName);
            using var table = layer.GetTable();
            using var definition = table.GetDefinition();
            var rules = DescribeDataRules(table, definition);
            return (object)new
            {
                map = layer.Map.Name,
                layer = layer.Name,
                table = definition.GetName(),
                alias = SafeString(definition.GetAliasName),
                object_id_field = SafeBool(definition.HasObjectID)
                    ? SafeString(definition.GetObjectIDField)
                    : null,
                global_id_field = SafeBool(definition.HasGlobalID)
                    ? SafeString(definition.GetGlobalIDField)
                    : null,
                subtype_field = SafeString(definition.GetSubtypeField),
                has_joins = layer.HasJoins,
                has_relates = layer.HasRelates,
                select_related_data = layer.IsSelectRelatedData,
                fields = definition.GetFields().Select(field => new
                {
                    name = field.Name,
                    alias = field.AliasName,
                    type = field.FieldType.ToString(),
                    length = field.Length,
                    precision = field.Precision,
                    scale = field.Scale,
                    nullable = field.IsNullable,
                    editable = field.IsEditable,
                    required = field.IsRequired,
                }).ToArray(),
                rules,
                source_data_modified = false,
            };
        });
    }

    private static string? SafeString(Func<string> getter)
    {
        try
        {
            return getter();
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static bool SafeBool(Func<bool> getter)
    {
        try
        {
            return getter();
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    internal static async Task<object> QueryAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var requestedFields = BridgeSupport.Strings(payload, "fields");
        var maximumRows = Math.Clamp(BridgeSupport.Int(payload, "max_rows", 100), 1, 5000);
        var includeGeometry = BridgeSupport.Bool(payload, "include_geometry", false);
        return await QueuedTask.Run<object>(() =>
        {
            var layer = ResolveFeatureLayer(requestedMap, layerName);
            using var table = layer.GetTable();
            using var definition = table.GetDefinition();
            var availableFields = definition.GetFields().Select(field => field.Name).ToArray();
            var fields = requestedFields.Length == 0
                ? availableFields
                : requestedFields.Where(field => availableFields.Contains(
                    field, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (fields.Length == 0)
            {
                throw new ArgumentException("No requested fields exist in the layer schema.");
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
            var filter = BuildFilter(payload, queryFields, maximumRows);
            var totalMatching = table.GetCount(new QueryFilter
            {
                WhereClause = filter.WhereClause,
                ObjectIDs = filter.ObjectIDs,
            });
            var rows = new List<Dictionary<string, object?>>();
            using var cursor = table.Search(filter, true);
            while (rows.Count < maximumRows && cursor.MoveNext())
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
                map = layer.Map.Name,
                layer = layer.Name,
                where_clause = filter.WhereClause,
                fields,
                total_matching = totalMatching,
                returned = rows.Count,
                truncated = totalMatching > rows.Count,
                rows,
            };
        });
    }

    internal static async Task<object> SummarizeFieldAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var fieldName = BridgeSupport.String(payload, "field")
            ?? throw new ArgumentException("field is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var maximumRows = Math.Clamp(BridgeSupport.Int(payload, "max_rows", 100000), 1, 1000000);
        return await QueuedTask.Run<object>(() =>
        {
            var layer = ResolveFeatureLayer(requestedMap, layerName);
            using var table = layer.GetTable();
            using var definition = table.GetDefinition();
            var field = definition.GetFields().FirstOrDefault(candidate => string.Equals(
                candidate.Name, fieldName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Field not found: {fieldName}");
            var filter = BuildFilter(payload, new[] { field.Name }, maximumRows);
            var totalMatching = table.GetCount(new QueryFilter { WhereClause = filter.WhereClause });
            long scanned = 0;
            long nullCount = 0;
            var numericCount = 0L;
            var sum = 0.0;
            var sumSquares = 0.0;
            double? minimum = null;
            double? maximum = null;
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            using var cursor = table.Search(filter, true);
            while (scanned < maximumRows && cursor.MoveNext())
            {
                scanned++;
                using var row = cursor.Current;
                var value = row[field.Name];
                if (value is null or DBNull)
                {
                    nullCount++;
                    continue;
                }
                if (distinct.Count < 10000)
                {
                    distinct.Add(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                }
                if (!TryDouble(value, out var number))
                {
                    continue;
                }
                numericCount++;
                sum += number;
                sumSquares += number * number;
                minimum = minimum is null ? number : Math.Min(minimum.Value, number);
                maximum = maximum is null ? number : Math.Max(maximum.Value, number);
            }
            var mean = numericCount > 0 ? sum / numericCount : (double?)null;
            var variance = numericCount > 0
                ? Math.Max(0, sumSquares / numericCount - mean!.Value * mean.Value)
                : (double?)null;
            return (object)new
            {
                map = layer.Map.Name,
                layer = layer.Name,
                field = field.Name,
                field_type = field.FieldType.ToString(),
                where_clause = filter.WhereClause,
                total_matching = totalMatching,
                scanned,
                truncated = totalMatching > scanned,
                null_count = nullCount,
                numeric_count = numericCount,
                distinct_count_lower_bound = distinct.Count,
                distinct_count_capped = distinct.Count >= 10000,
                minimum,
                maximum,
                sum = numericCount > 0 ? sum : (double?)null,
                mean,
                standard_deviation = variance is null ? (double?)null : Math.Sqrt(variance.Value),
            };
        });
    }

    internal static async Task<object> SelectAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var methodText = BridgeSupport.String(payload, "combination", "New")!;
        if (!Enum.TryParse<SelectionCombinationMethod>(methodText, true, out var method))
        {
            throw new ArgumentException($"Unsupported selection combination: {methodText}");
        }
        var maximumIds = Math.Clamp(BridgeSupport.Int(payload, "max_ids", 1000), 0, 10000);
        return await QueuedTask.Run<object>(() =>
        {
            var layer = ResolveFeatureLayer(requestedMap, layerName);
            var filter = BuildFilter(payload, new[] { "*" }, 0);
            using var selection = layer.Select(filter, method);
            var ids = selection.GetObjectIDs();
            return (object)new
            {
                map = layer.Map.Name,
                layer = layer.Name,
                combination = method.ToString(),
                selected_count = ids.Count,
                object_ids = ids.Take(maximumIds).ToArray(),
                object_ids_truncated = ids.Count > maximumIds,
            };
        });
    }

    internal static async Task<object> GetSelectionAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var requestedLayers = BridgeSupport.Strings(payload, "layer_names");
        var maximumIds = Math.Clamp(BridgeSupport.Int(payload, "max_ids", 1000), 0, 10000);
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layers = map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                .Where(layer => requestedLayers.Length == 0 || requestedLayers.Contains(
                    layer.Name, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            return (object)new
            {
                map = map.Name,
                layers = layers.Select(layer =>
                {
                    using var selection = layer.GetSelection();
                    var ids = selection.GetObjectIDs();
                    return new
                    {
                        layer = layer.Name,
                        selected_count = ids.Count,
                        object_ids = ids.Take(maximumIds).ToArray(),
                        object_ids_truncated = ids.Count > maximumIds,
                    };
                }).ToArray(),
            };
        });
    }

    internal static async Task<object> ClearSelectionAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var requestedLayers = BridgeSupport.Strings(payload, "layer_names");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            if (requestedLayers.Length == 0)
            {
                map.ClearSelection();
                return (object)new { map = map.Name, cleared_all = true, layers = Array.Empty<string>() };
            }
            var layers = map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                .Where(layer => requestedLayers.Contains(layer.Name, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            foreach (var layer in layers)
            {
                layer.ClearSelection();
            }
            return (object)new
            {
                map = map.Name,
                cleared_all = false,
                layers = layers.Select(layer => layer.Name).ToArray(),
            };
        });
    }

    internal static async Task<object> TableSchemaAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var tableName = BridgeSupport.String(payload, "table_name")
            ?? throw new ArgumentException("table_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(() =>
        {
            var member = ResolveStandaloneTable(requestedMap, tableName);
            using var table = member.GetTable();
            using var definition = table.GetDefinition();
            var rules = DescribeDataRules(table, definition);
            return (object)new
            {
                map = member.Map.Name,
                table = member.Name,
                source_table = definition.GetName(),
                alias = definition.GetAliasName(),
                object_id_field = definition.HasObjectID() ? definition.GetObjectIDField() : null,
                global_id_field = definition.HasGlobalID() ? definition.GetGlobalIDField() : null,
                subtype_field = definition.GetSubtypeField(),
                has_joins = member.HasJoins,
                has_relates = member.HasRelates,
                select_related_data = member.IsSelectRelatedData,
                fields = definition.GetFields().Select(field => new
                {
                    name = field.Name,
                    alias = field.AliasName,
                    type = field.FieldType.ToString(),
                    length = field.Length,
                    precision = field.Precision,
                    scale = field.Scale,
                    nullable = field.IsNullable,
                    editable = field.IsEditable,
                    required = field.IsRequired,
                }).ToArray(),
                rules,
                source_data_modified = false,
            };
        });
    }

    private static object DescribeDataRules(Table table, TableDefinition definition)
    {
        var errors = new List<string>();
        object[] subtypes;
        object[] indexes;
        object[] attributeRules;
        object[] contingencies;
        object[] controllerDatasets;

        try
        {
            subtypes = definition.GetSubtypes().Take(1000).Select(subtype => (object)new
            {
                code = subtype.GetCode(),
                name = subtype.GetName(),
                is_default = subtype.GetCode() == SafeInt(definition.GetDefaultSubtypeCode),
            }).ToArray();
        }
        catch (Exception exception)
        {
            errors.Add($"subtypes: {exception.Message}");
            subtypes = Array.Empty<object>();
        }

        try
        {
            indexes = definition.GetIndexes().Take(1000).Select(index => (object)new
            {
                name = index.GetName(),
                unique = index.IsUnique(),
                ascending = index.IsAscending(),
                full_text = index.IsFullText(),
                fields = index.GetFields().Select(field => field.Name).ToArray(),
            }).ToArray();
        }
        catch (Exception exception)
        {
            errors.Add($"indexes: {exception.Message}");
            indexes = Array.Empty<object>();
        }

        try
        {
            attributeRules = definition.GetAttributeRules(AttributeRuleType.All).Take(2000)
                .Select(rule => (object)new
                {
                    name = rule.GetName(),
                    description = rule.GetDescription(),
                    rule_type = rule.GetAttributeRuleType().ToString(),
                    enabled = rule.GetIsEnabled(),
                    batch = rule.GetIsBatch(),
                    field = rule.GetFieldName(),
                    field_editable = rule.GetIsFieldEditable(),
                    evaluation_order = rule.GetEvaluationOrder(),
                    triggering_events = rule.GetTriggeringEvents().ToString(),
                    severity = rule.GetSeverity(),
                    error_number = rule.GetErrorNumber(),
                    error_message = rule.GetErrorMessage(),
                    minimum_arcade_version = rule.GetMinimumArcadeVersion(),
                    exclude_from_client_evaluation = rule.GetExcludeFromClientEvaluation(),
                    subtype = SafeSubtype(rule.GetSubtype),
                    tags = rule.GetTags().Take(100).ToArray(),
                    script_expression = rule.GetScriptExpression(),
                }).ToArray();
        }
        catch (Exception exception)
        {
            errors.Add($"attribute_rules: {exception.Message}");
            attributeRules = Array.Empty<object>();
        }

        try
        {
            contingencies = definition.GetContingencies().Take(2000).Select(item => (object)new
            {
                id = item.ID,
                retired = item.IsRetired,
                field_group = item.FieldGroup is null ? null : new
                {
                    name = item.FieldGroup.Name,
                    restrictive = item.FieldGroup.IsRestrictive,
                    fields = item.FieldGroup.FieldNames.ToArray(),
                },
                subtype = item.Subtype is null ? null : new
                {
                    code = item.Subtype.GetCode(),
                    name = item.Subtype.GetName(),
                },
                value_count = item.GetContingentValues().Count,
                values = item.GetContingentValues().Take(100).Select(pair => new
                {
                    field = pair.Key,
                    value = pair.Value?.ToString(),
                }).ToArray(),
            }).ToArray();
        }
        catch (Exception exception)
        {
            errors.Add($"contingencies: {exception.Message}");
            contingencies = Array.Empty<object>();
        }

        try
        {
            controllerDatasets = table.IsControllerDatasetSupported()
                ? table.GetControllerDatasets().Take(1000).Select(item => (object)new
                {
                    name = item.GetName(),
                    type = item.Type.ToString(),
                    path = item.GetPath()?.ToString(),
                }).ToArray()
                : Array.Empty<object>();
        }
        catch (Exception exception)
        {
            errors.Add($"controller_datasets: {exception.Message}");
            controllerDatasets = Array.Empty<object>();
        }

        return new
        {
            dataset_type = definition.DatasetType.ToString(),
            joined_table = SafeBool(table.IsJoinedTable),
            archive_enabled = SafeBool(table.IsArchiveEnabled),
            attachments_enabled = SafeBool(table.IsAttachmentEnabled),
            editor_tracking_enabled = SafeBool(definition.IsEditorTrackingEnabled),
            time_in_utc = SafeBool(definition.IsTimeInUTC),
            validation_status_field = SafeString(definition.GetValidationStatusField),
            editor_tracking_fields = new
            {
                creator = SafeString(definition.GetCreatorField),
                created_at = SafeString(definition.GetCreatedAtField),
                editor = SafeString(definition.GetEditorField),
                edited_at = SafeString(definition.GetEditedAtField),
            },
            default_subtype_code = SafeInt(definition.GetDefaultSubtypeCode),
            subtypes,
            indexes,
            attribute_rules = attributeRules,
            contingencies,
            controller_datasets = controllerDatasets,
            inspection_errors = errors,
        };
    }

    private static int? SafeInt(Func<int> getter)
    {
        try
        {
            return getter();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static object? SafeSubtype(Func<Subtype> getter)
    {
        try
        {
            var subtype = getter();
            return subtype is null ? null : new { code = subtype.GetCode(), name = subtype.GetName() };
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static async Task<object> QueryTableAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var tableName = BridgeSupport.String(payload, "table_name")
            ?? throw new ArgumentException("table_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var requestedFields = BridgeSupport.Strings(payload, "fields");
        var maximumRows = Math.Clamp(BridgeSupport.Int(payload, "max_rows", 100), 1, 5000);
        return await QueuedTask.Run<object>(() =>
        {
            var member = ResolveStandaloneTable(requestedMap, tableName);
            using var table = member.GetTable();
            using var definition = table.GetDefinition();
            var availableFields = definition.GetFields().Select(field => field.Name).ToArray();
            var fields = requestedFields.Length == 0
                ? availableFields
                : requestedFields.Where(field => availableFields.Contains(
                    field, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (fields.Length == 0)
            {
                throw new ArgumentException("No requested fields exist in the table schema.");
            }
            var filter = BuildFilter(payload, fields, maximumRows);
            var countFilter = new QueryFilter
            {
                WhereClause = filter.WhereClause,
                ObjectIDs = filter.ObjectIDs,
            };
            var totalMatching = table.GetCount(countFilter);
            var hasObjectId = definition.HasObjectID();
            var rows = new List<Dictionary<string, object?>>();
            using var cursor = table.Search(filter, true);
            while (rows.Count < maximumRows && cursor.MoveNext())
            {
                using var row = cursor.Current;
                var values = fields.ToDictionary(
                    field => field,
                    field => JsonValue(row[field]),
                    StringComparer.OrdinalIgnoreCase);
                if (hasObjectId)
                {
                    values["__object_id"] = row.GetObjectID();
                }
                rows.Add(values);
            }
            return (object)new
            {
                map = member.Map.Name,
                table = member.Name,
                where_clause = filter.WhereClause,
                fields,
                total_matching = totalMatching,
                returned = rows.Count,
                truncated = totalMatching > rows.Count,
                rows,
            };
        });
    }

    private static FeatureLayer ResolveFeatureLayer(string? requestedMap, string layerName)
    {
        var map = BridgeSupport.ResolveMap(requestedMap);
        return BridgeSupport.ResolveLayer(map, layerName) as FeatureLayer
            ?? throw new InvalidOperationException($"Feature layer not found: {layerName}");
    }

    private static StandaloneTable ResolveStandaloneTable(string? requestedMap, string tableName)
    {
        var map = BridgeSupport.ResolveMap(requestedMap);
        return map.GetStandaloneTablesAsFlattenedList()
            .FirstOrDefault(table => string.Equals(
                table.Name, tableName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Standalone table not found: {tableName}");
    }

    private static QueryFilter BuildFilter(
        JsonElement payload,
        IReadOnlyList<string> fields,
        int rowCount)
    {
        var objectIds = payload.TryGetProperty("object_ids", out var ids)
            && ids.ValueKind == JsonValueKind.Array
            ? ids.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Number)
                .Select(item => item.GetInt64())
                .ToArray()
            : Array.Empty<long>();
        return new QueryFilter
        {
            WhereClause = BridgeSupport.String(payload, "where_clause", "1=1"),
            SubFields = fields.Count == 1 && fields[0] == "*" ? "*" : string.Join(",", fields),
            PrefixClause = BridgeSupport.String(payload, "prefix_clause", string.Empty),
            PostfixClause = BridgeSupport.String(payload, "postfix_clause", string.Empty),
            ObjectIDs = objectIds,
            RowCount = rowCount,
        };
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

    private static bool TryDouble(object value, out double number)
    {
        try
        {
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return !double.IsNaN(number) && !double.IsInfinity(number);
        }
        catch
        {
            number = 0;
            return false;
        }
    }
}
