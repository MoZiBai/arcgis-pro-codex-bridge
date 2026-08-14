using System.Globalization;
using System.IO;
using System.Text.Json;
using ArcGIS.Core.Data;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class AttachmentRelationshipActions
{
    internal static async Task<object> ListAttachmentsAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var memberName = BridgeSupport.String(payload, "map_member_name")
            ?? throw new ArgumentException("map_member_name is required");
        var objectIds = Longs(payload, "object_ids");
        if (objectIds.Length == 0)
        {
            throw new ArgumentException("object_ids must contain at least one object ID");
        }
        var maximum = Math.Clamp(BridgeSupport.Int(payload, "max_attachments", 1000), 1, 10000);
        return await QueuedTask.Run<object>(() =>
        {
            var resolved = ResolveMember(BridgeSupport.String(payload, "map_name"), memberName);
            using var table = resolved.table;
            if (!table.IsAttachmentEnabled())
            {
                return (object)new
                {
                    map = resolved.map.Name,
                    map_member = resolved.member.Name,
                    attachment_enabled = false,
                    attachments = Array.Empty<object>(),
                };
            }
            var results = new List<object>();
            using var cursor = table.Search(new QueryFilter { ObjectIDs = objectIds }, false);
            while (results.Count < maximum && cursor.MoveNext())
            {
                using var row = cursor.Current;
                var attachments = row.GetAttachments(null!, true);
                foreach (var attachment in attachments)
                {
                    using (attachment)
                    {
                        if (results.Count >= maximum)
                        {
                            break;
                        }
                        results.Add(AttachmentInfo(row.GetObjectID(), attachment));
                    }
                }
            }
            return (object)new
            {
                map = resolved.map.Name,
                map_member = resolved.member.Name,
                attachment_enabled = true,
                object_ids = objectIds,
                returned = results.Count,
                truncated = results.Count >= maximum,
                attachments = results,
            };
        });
    }

    internal static async Task<object> ExportAttachmentAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var memberName = BridgeSupport.String(payload, "map_member_name")
            ?? throw new ArgumentException("map_member_name is required");
        var objectId = Long(payload, "object_id");
        var attachmentId = Long(payload, "attachment_id");
        var output = BridgeSupport.String(payload, "output_file")
            ?? throw new ArgumentException("output_file is required");
        var overwrite = BridgeSupport.Bool(payload, "overwrite", false);
        if (File.Exists(output) && !overwrite)
        {
            throw new IOException($"Output already exists and overwrite=false: {output}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? string.Empty);
        return await QueuedTask.Run<object>(() =>
        {
            var resolved = ResolveMember(BridgeSupport.String(payload, "map_name"), memberName);
            using var table = resolved.table;
            using var cursor = table.Search(new QueryFilter { ObjectIDs = new[] { objectId } }, false);
            if (!cursor.MoveNext())
            {
                throw new InvalidOperationException($"Row not found: {objectId}");
            }
            using var row = cursor.Current;
            var attachments = row.GetAttachments(new[] { attachmentId }, false);
            var attachment = attachments.FirstOrDefault()
                ?? throw new InvalidOperationException($"Attachment not found: {attachmentId}");
            using (attachment)
            using (var data = attachment.GetData())
            using (var file = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                data.CopyTo(file);
                return (object)new
                {
                    map = resolved.map.Name,
                    map_member = resolved.member.Name,
                    object_id = objectId,
                    attachment = AttachmentInfo(objectId, attachment),
                    output,
                    bytes_written = file.Length,
                    source_data_modified = false,
                };
            }
        });
    }

    internal static async Task<object> AddAttachmentAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        RequireSourceEditConfirmation(payload);
        var memberName = BridgeSupport.String(payload, "map_member_name")
            ?? throw new ArgumentException("map_member_name is required");
        var objectId = Long(payload, "object_id");
        var filePath = BridgeSupport.String(payload, "file_path")
            ?? throw new ArgumentException("file_path is required");
        var setup = await QueuedTask.Run(() =>
        {
            var resolved = ResolveMember(BridgeSupport.String(payload, "map_name"), memberName);
            resolved.table.Dispose();
            var operation = new EditOperation
            {
                Name = BridgeSupport.String(payload, "operation_name", $"Add attachment to {memberName}")!,
            };
            operation.AddAttachment(resolved.member, objectId, filePath);
            return (operation: operation, mapName: resolved.map.Name, memberName: resolved.member.Name);
        });
        return await ExecuteEditAsync(setup.operation, setup.mapName, setup.memberName, "add", objectId);
    }

    internal static async Task<object> UpdateAttachmentAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        RequireSourceEditConfirmation(payload);
        var memberName = BridgeSupport.String(payload, "map_member_name")
            ?? throw new ArgumentException("map_member_name is required");
        var objectId = Long(payload, "object_id");
        var attachmentId = Long(payload, "attachment_id");
        var filePath = BridgeSupport.String(payload, "file_path")
            ?? throw new ArgumentException("file_path is required");
        var setup = await QueuedTask.Run(() =>
        {
            var resolved = ResolveMember(BridgeSupport.String(payload, "map_name"), memberName);
            resolved.table.Dispose();
            var operation = new EditOperation
            {
                Name = BridgeSupport.String(payload, "operation_name", $"Update attachment on {memberName}")!,
            };
            operation.UpdateAttachment(resolved.member, objectId, attachmentId, filePath);
            return (operation: operation, mapName: resolved.map.Name, memberName: resolved.member.Name);
        });
        var result = await ExecuteEditAsync(setup.operation, setup.mapName, setup.memberName, "update", objectId);
        return new { result, attachment_id = attachmentId };
    }

    internal static async Task<object> RemoveAttachmentsAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        RequireSourceEditConfirmation(payload);
        if (!BridgeSupport.Bool(payload, "confirm_delete", false))
        {
            throw new ArgumentException("confirm_delete=true is required to remove attachments");
        }
        var memberName = BridgeSupport.String(payload, "map_member_name")
            ?? throw new ArgumentException("map_member_name is required");
        var objectId = Long(payload, "object_id");
        var attachmentIds = Longs(payload, "attachment_ids");
        if (attachmentIds.Length == 0)
        {
            throw new ArgumentException("attachment_ids must contain at least one attachment ID");
        }
        var setup = await QueuedTask.Run(() =>
        {
            var resolved = ResolveMember(BridgeSupport.String(payload, "map_name"), memberName);
            resolved.table.Dispose();
            var operation = new EditOperation
            {
                Name = BridgeSupport.String(payload, "operation_name", $"Remove attachments from {memberName}")!,
            };
            foreach (var attachmentId in attachmentIds)
            {
                operation.RemoveAttachment(resolved.member, objectId, attachmentId);
            }
            return (operation: operation, mapName: resolved.map.Name, memberName: resolved.member.Name);
        });
        var success = await setup.operation.ExecuteAsync();
        return new
        {
            map = setup.mapName,
            map_member = setup.memberName,
            operation = "remove",
            object_id = objectId,
            attachment_ids = attachmentIds,
            success,
            error = success ? null : setup.operation.ErrorMessage,
            source_data_edits_staged = success,
            edits_saved = false,
        };
    }

    internal static async Task<object> ListRelationshipsAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var memberName = BridgeSupport.String(payload, "map_member_name")
            ?? throw new ArgumentException("map_member_name is required");
        var includeAll = BridgeSupport.Bool(payload, "include_all_geodatabase_relationships", false);
        return await QueuedTask.Run<object>(() =>
        {
            var resolved = ResolveMember(BridgeSupport.String(payload, "map_name"), memberName);
            using var table = resolved.table;
            using var tableDefinition = table.GetDefinition();
            using var datastore = table.GetDatastore();
            if (datastore is not Geodatabase geodatabase)
            {
                return (object)new
                {
                    map = resolved.map.Name,
                    map_member = resolved.member.Name,
                    geodatabase_supported = false,
                    relationships = Array.Empty<object>(),
                };
            }
            var tableName = tableDefinition.GetName();
            var relationships = new List<object>();
            foreach (var definition in geodatabase.GetDefinitions<RelationshipClassDefinition>())
            {
                using (definition)
                {
                    var related = SameDataset(definition.GetOriginClass(), tableName)
                        || SameDataset(definition.GetDestinationClass(), tableName);
                    if (!includeAll && !related)
                    {
                        continue;
                    }
                    relationships.Add(new
                    {
                        name = definition.GetName(),
                        alias = definition.GetAliasName(),
                        origin_class = definition.GetOriginClass(),
                        destination_class = definition.GetDestinationClass(),
                        cardinality = definition.GetCardinality().ToString(),
                        composite = definition.IsComposite(),
                        attachment_relationship = definition.IsAttachmentRelationship(),
                        forward_label = definition.GetForwardPathLabel(),
                        backward_label = definition.GetBackwardPathLabel(),
                        related_to_requested_member = related,
                    });
                }
            }
            return (object)new
            {
                map = resolved.map.Name,
                map_member = resolved.member.Name,
                source_table = tableName,
                geodatabase_supported = true,
                relationships,
            };
        });
    }

    internal static async Task<object> QueryRelatedRecordsAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var memberName = BridgeSupport.String(payload, "map_member_name")
            ?? throw new ArgumentException("map_member_name is required");
        var relationshipName = BridgeSupport.String(payload, "relationship_name")
            ?? throw new ArgumentException("relationship_name is required");
        var objectIds = Longs(payload, "object_ids");
        if (objectIds.Length == 0)
        {
            throw new ArgumentException("object_ids must contain at least one object ID");
        }
        var direction = BridgeSupport.String(payload, "direction", "origin_to_destination")!.ToLowerInvariant();
        var maximum = Math.Clamp(BridgeSupport.Int(payload, "max_rows", 100), 1, 5000);
        var requestedFields = BridgeSupport.Strings(payload, "fields");
        return await QueuedTask.Run<object>(() =>
        {
            var resolved = ResolveMember(BridgeSupport.String(payload, "map_name"), memberName);
            using var table = resolved.table;
            using var datastore = table.GetDatastore();
            if (datastore is not Geodatabase geodatabase)
            {
                throw new InvalidOperationException("Relationship traversal requires a geodatabase-backed map member.");
            }
            using var relationship = geodatabase.OpenDataset<RelationshipClass>(relationshipName);
            var relatedRows = direction switch
            {
                "origin_to_destination" => relationship.GetRowsRelatedToOriginRows(objectIds),
                "destination_to_origin" => relationship.GetRowsRelatedToDestinationRows(objectIds),
                _ => throw new ArgumentException($"Unsupported relationship direction: {direction}"),
            };
            try
            {
                var rows = new List<Dictionary<string, object?>>();
                string[] fields = Array.Empty<string>();
                foreach (var row in relatedRows.Take(maximum))
                {
                    if (fields.Length == 0)
                    {
                        var available = row.GetFields().Select(field => field.Name).ToArray();
                        fields = requestedFields.Length == 0
                            ? available
                            : requestedFields.Where(field => available.Contains(
                                field, StringComparer.OrdinalIgnoreCase)).ToArray();
                    }
                    var values = fields.ToDictionary(
                        field => field,
                        field => JsonValue(row[field]),
                        StringComparer.OrdinalIgnoreCase);
                    values["__object_id"] = row.GetObjectID();
                    rows.Add(values);
                }
                return (object)new
                {
                    map = resolved.map.Name,
                    map_member = resolved.member.Name,
                    relationship = relationship.GetName(),
                    direction,
                    input_object_ids = objectIds,
                    fields,
                    total_related = relatedRows.Count,
                    returned = rows.Count,
                    truncated = relatedRows.Count > rows.Count,
                    rows,
                };
            }
            finally
            {
                foreach (var row in relatedRows)
                {
                    row.Dispose();
                }
            }
        });
    }

    private static async Task<object> ExecuteEditAsync(
        EditOperation operation,
        string map,
        string member,
        string action,
        long objectId)
    {
        var success = await operation.ExecuteAsync();
        return new
        {
            map,
            map_member = member,
            operation = action,
            object_id = objectId,
            success,
            error = success ? null : operation.ErrorMessage,
            source_data_edits_staged = success,
            edits_saved = false,
        };
    }

    private static (Map map, MapMember member, Table table) ResolveMember(
        string? requestedMap,
        string memberName)
    {
        var map = BridgeSupport.ResolveMap(requestedMap);
        var layer = map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
            .FirstOrDefault(candidate => string.Equals(
                candidate.Name, memberName, StringComparison.OrdinalIgnoreCase));
        if (layer is not null)
        {
            return (map, layer, layer.GetTable());
        }
        var standalone = map.GetStandaloneTablesAsFlattenedList()
            .FirstOrDefault(candidate => string.Equals(
                candidate.Name, memberName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Feature layer or standalone table not found: {memberName}");
        return (map, standalone, standalone.GetTable());
    }

    private static object AttachmentInfo(long objectId, Attachment attachment) => new
    {
        object_id = objectId,
        attachment_id = attachment.GetAttachmentID(),
        name = attachment.GetName(),
        content_type = attachment.GetContentType(),
        size = attachment.GetSize(),
        global_id = attachment.GetGlobalID().ToString(),
        parent_global_id = attachment.GetParentGlobalID().ToString(),
    };

    private static long Long(JsonElement payload, string name)
    {
        if (payload.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var result))
        {
            return result;
        }
        throw new ArgumentException($"{name} is required");
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

    private static bool SameDataset(string left, string right)
    {
        static string Tail(string value) => value.Split('.').Last();
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Tail(left), Tail(right), StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireSourceEditConfirmation(JsonElement payload)
    {
        if (!BridgeSupport.Bool(payload, "confirm_source_edit", false))
        {
            throw new ArgumentException("confirm_source_edit=true is required for source-data editing");
        }
    }
}
