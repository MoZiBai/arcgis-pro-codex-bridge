using System.Text.Json;
using System.IO;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class LayerActions
{
    internal static async Task<object> DescribeAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var includeCim = BridgeSupport.Bool(payload, "include_cim", false);

        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = BridgeSupport.ResolveLayer(map, layerName);
            string? path = null;
            try
            {
                path = layer.GetPath()?.LocalPath ?? layer.GetPath()?.ToString();
            }
            catch
            {
                // Service and graphics layers do not always expose a dataset path.
            }

            object? renderer = null;
            if (layer is BasicRasterLayer rasterLayer)
            {
                renderer = SymbologyActions.DescribeRasterColorizer(rasterLayer.GetColorizer());
            }
            else if (layer is FeatureLayer featureLayer)
            {
                var featureRenderer = featureLayer.GetRenderer();
                renderer = new
                {
                    type = featureRenderer?.GetType().Name,
                    cim_json = includeCim ? featureRenderer?.ToJson() : null,
                };
            }

            return (object)new
            {
                map = map.Name,
                layer = layer.Name,
                type = layer.GetType().Name,
                uri = layer.URI,
                path,
                visible = layer.IsVisible,
                transparency = layer.Transparency,
                min_scale = layer.MinScale,
                max_scale = layer.MaxScale,
                blending_mode = layer.BlendingMode.ToString(),
                renderer,
                cim_json = includeCim ? layer.GetDefinition().ToJson() : null,
            };
        });
    }

    internal static async Task<object> GetCimAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = BridgeSupport.ResolveLayer(map, layerName);
            var definition = layer.GetDefinition();
            return (object)new
            {
                map = map.Name,
                layer = layer.Name,
                cim_type = definition.GetType().Name,
                cim_json = definition.ToJson(),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> SetCimAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var cimJson = BridgeSupport.String(payload, "cim_json")
            ?? throw new ArgumentException("cim_json is required");
        if (!BridgeSupport.Bool(payload, "confirm_replace_definition"))
        {
            throw new ArgumentException("confirm_replace_definition=true is required");
        }

        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = BridgeSupport.ResolveLayer(map, layerName);
            var current = layer.GetDefinition();
            var replacement = ParseLayerDefinition(current, cimJson);
            PreserveLayerIdentityAndSource(current, replacement);
            layer.SetDefinition(replacement);
            var applied = layer.GetDefinition();
            return (object)new
            {
                map = map.Name,
                layer = layer.Name,
                previous_cim_type = current.GetType().Name,
                cim_type = applied.GetType().Name,
                cim_json = applied.ToJson(),
                data_connection_preserved = true,
                project_structure_preserved = true,
                source_data_modified = false,
            };
        });
    }

    private static CIMBaseLayer ParseLayerDefinition(CIMBaseLayer current, string cimJson)
    {
        var method = current.GetType().GetMethod(
            "FromJson",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
            null,
            new[] { typeof(string), typeof(JsonDeserializationSettings) },
            null)
            ?? throw new InvalidOperationException(
                $"Layer CIM type does not expose FromJson: {current.GetType().Name}");
        var parsed = method.Invoke(
            null,
            new object[] { cimJson, new JsonDeserializationSettings() }) as CIMBaseLayer
            ?? throw new ArgumentException(
                $"Could not parse layer CIM as {current.GetType().Name}");
        if (parsed.GetType() != current.GetType())
        {
            throw new ArgumentException(
                $"Layer CIM type cannot change from {current.GetType().Name} to {parsed.GetType().Name}");
        }
        return parsed;
    }

    private static void PreserveLayerIdentityAndSource(
        CIMBaseLayer current,
        CIMBaseLayer replacement)
    {
        replacement.URI = current.URI;
        replacement.SourceURI = current.SourceURI;
        replacement.SourcePortalUrl = current.SourcePortalUrl;
        switch (current, replacement)
        {
            case (CIMFeatureLayer oldLayer, CIMFeatureLayer newLayer):
                if (oldLayer.FeatureTable is not null && newLayer.FeatureTable is not null)
                {
                    newLayer.FeatureTable.DataConnection = oldLayer.FeatureTable.DataConnection;
                }
                break;
            case (CIMRasterLayer oldLayer, CIMRasterLayer newLayer):
                newLayer.DataConnection = oldLayer.DataConnection;
                break;
            case (CIMGroupLayer oldLayer, CIMGroupLayer newLayer):
                newLayer.Layers = oldLayer.Layers;
                newLayer.StandaloneTables = oldLayer.StandaloneTables;
                break;
            case (CIMGraphicsLayer oldLayer, CIMGraphicsLayer newLayer):
                newLayer.ElementStorageURI = oldLayer.ElementStorageURI;
                break;
        }
    }

    internal static async Task<object> DuplicateAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var copyName = BridgeSupport.String(payload, "new_name");
        var groupName = BridgeSupport.String(payload, "group_name");

        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var source = BridgeSupport.ResolveLayer(map, layerName);
            if (!LayerFactory.Instance.CanCopyLayer(source))
            {
                throw new InvalidOperationException($"Layer cannot be copied: {layerName}");
            }

            ILayerContainerEdit container = map;
            var maximumIndex = map.Layers.Count;
            if (!string.IsNullOrWhiteSpace(groupName))
            {
                var group = map.GetLayersAsFlattenedList()
                    .OfType<GroupLayer>()
                    .FirstOrDefault(layer => string.Equals(
                        layer.Name, groupName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Group layer not found: {groupName}");
                container = group;
                maximumIndex = group.Layers.Count;
            }
            var index = Math.Clamp(BridgeSupport.Int(payload, "index", 0), 0, maximumIndex);
            var copy = LayerFactory.Instance.CopyLayer(source, container, index);
            if (!string.IsNullOrWhiteSpace(copyName))
            {
                copy.SetName(copyName);
            }
            return (object)new
            {
                map = map.Name,
                source_layer = source.Name,
                copied_layer = copy.Name,
                group = groupName,
                index,
                source_data_copied = false,
            };
        });
    }

    internal static async Task<object> CreateGroupAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var groupName = BridgeSupport.String(payload, "group_name")
            ?? throw new ArgumentException("group_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var parentName = BridgeSupport.String(payload, "parent_group_name");

        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            ILayerContainerEdit container = map;
            var maximumIndex = map.Layers.Count;
            if (!string.IsNullOrWhiteSpace(parentName))
            {
                var parent = map.GetLayersAsFlattenedList()
                    .OfType<GroupLayer>()
                    .FirstOrDefault(layer => string.Equals(
                        layer.Name, parentName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Parent group layer not found: {parentName}");
                container = parent;
                maximumIndex = parent.Layers.Count;
            }
            var index = Math.Clamp(BridgeSupport.Int(payload, "index", maximumIndex), 0, maximumIndex);
            var group = LayerFactory.Instance.CreateGroupLayer(container, index, groupName);
            return (object)new
            {
                map = map.Name,
                group = group.Name,
                parent_group = parentName,
                index,
            };
        });
    }

    internal static async Task<object> ManageAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var requestedNames = BridgeSupport.Strings(payload, "layer_names");
        var exceptNames = BridgeSupport.Strings(payload, "except_layer_names")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetAll = BridgeSupport.Bool(payload, "all", requestedNames.Length == 0);

        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layers = map.GetLayersAsFlattenedList()
                .Where(layer => !exceptNames.Contains(layer.Name))
                .Where(layer => targetAll || requestedNames.Contains(
                    layer.Name, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (layers.Length == 0)
            {
                throw new InvalidOperationException("No matching layers were found.");
            }
            var newName = BridgeSupport.String(payload, "name");
            if (!string.IsNullOrWhiteSpace(newName) && layers.Length != 1)
            {
                throw new ArgumentException("Renaming requires exactly one matching layer.");
            }

            var changed = new List<object>();
            foreach (var layer in layers)
            {
                if (!string.IsNullOrWhiteSpace(newName))
                {
                    layer.SetName(newName);
                }
                if (payload.TryGetProperty("visible", out var visible)
                    && visible.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    layer.SetVisibility(visible.GetBoolean());
                }
                if (payload.TryGetProperty("transparency", out var transparency)
                    && transparency.ValueKind == JsonValueKind.Number)
                {
                    layer.SetTransparency(Math.Clamp(transparency.GetDouble(), 0, 100));
                }
                if (payload.TryGetProperty("min_scale", out var minScale)
                    && minScale.ValueKind == JsonValueKind.Number)
                {
                    layer.SetMinScale(minScale.GetDouble());
                }
                if (payload.TryGetProperty("max_scale", out var maxScale)
                    && maxScale.ValueKind == JsonValueKind.Number)
                {
                    layer.SetMaxScale(maxScale.GetDouble());
                }
                if (layer is FeatureLayer featureLayer
                    && payload.TryGetProperty("definition_query", out var query)
                    && query.ValueKind == JsonValueKind.String)
                {
                    featureLayer.SetDefinitionQuery(query.GetString() ?? string.Empty);
                }
                var blendMode = BridgeSupport.String(payload, "blend_mode");
                if (!string.IsNullOrWhiteSpace(blendMode))
                {
                    if (!Enum.TryParse<BlendingMode>(blendMode, true, out var parsedBlendMode))
                    {
                        throw new ArgumentException($"Unsupported blending mode: {blendMode}");
                    }
                    if (!layer.CanSetBlendingMode(parsedBlendMode))
                    {
                        throw new InvalidOperationException(
                            $"Layer does not support blending mode {parsedBlendMode}: {layer.Name}");
                    }
                    layer.SetBlendingMode(parsedBlendMode);
                }

                changed.Add(new
                {
                    layer = layer.Name,
                    visible = layer.IsVisible,
                    transparency = layer.Transparency,
                    blending_mode = layer.BlendingMode.ToString(),
                });
            }

            return (object)new { map = map.Name, changed };
        });
    }

    internal static async Task<object> RemoveAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var requestedNames = BridgeSupport.Strings(payload, "layer_names");
        if (requestedNames.Length == 0)
        {
            throw new ArgumentException("layer_names is required");
        }

        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layers = map.GetLayersAsFlattenedList()
                .Where(layer => requestedNames.Contains(layer.Name, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (layers.Length == 0)
            {
                throw new InvalidOperationException("No matching layers were found.");
            }
            map.RemoveLayers(layers);
            return (object)new
            {
                map = map.Name,
                removed_from_map = layers.Select(layer => layer.Name).ToArray(),
                source_data_deleted = false,
            };
        });
    }

    internal static async Task<object> ReorderAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var position = BridgeSupport.String(payload, "position", "top")!.ToLowerInvariant();
        var requestedIndex = BridgeSupport.Int(payload, "index", 0);

        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = BridgeSupport.ResolveLayer(map, layerName);
            var rootCount = map.Layers.Count;
            var index = position switch
            {
                "top" => 0,
                "bottom" => Math.Max(0, rootCount - 1),
                _ => Math.Clamp(requestedIndex, 0, Math.Max(0, rootCount - 1)),
            };
            map.MoveLayer(layer, index);
            return (object)new { map = map.Name, layer = layer.Name, index };
        });
    }

    internal static async Task<object> ZoomAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var factor = Math.Max(1.0, BridgeSupport.Double(payload, "scale_factor", 1.08));

        var resolved = await QueuedTask.Run(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            return (map, layer: BridgeSupport.ResolveLayer(map, layerName));
        });

        var view = MapView.Active;
        if (view is null || view.Map != resolved.map)
        {
            throw new InvalidOperationException("The requested map must be the active map view to zoom it.");
        }
        await view.ZoomToAsync(resolved.layer, false, TimeSpan.Zero, true);
        await QueuedTask.Run(() =>
        {
            var camera = view.Camera;
            camera.Scale *= factor;
            view.ZoomTo(camera, TimeSpan.Zero);
        });
        return new { map = resolved.map.Name, layer = resolved.layer.Name, scale_factor = factor };
    }

    internal static async Task<object> ConfigureLabelsAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var visible = BridgeSupport.Bool(payload, "visible", true);

        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = BridgeSupport.ResolveLayer(map, layerName) as FeatureLayer
                ?? throw new InvalidOperationException($"Feature layer not found: {layerName}");
            var labelClass = layer.LabelClasses.FirstOrDefault();
            if (labelClass is null)
            {
                layer.AddLabelClass("Codex Labels");
                labelClass = layer.LabelClasses.First();
            }

            var expression = BridgeSupport.String(payload, "expression");
            var field = BridgeSupport.String(payload, "field");
            if (string.IsNullOrWhiteSpace(expression) && !string.IsNullOrWhiteSpace(field))
            {
                expression = $"$feature.{field}";
            }
            if (!string.IsNullOrWhiteSpace(expression))
            {
                labelClass.SetExpressionEngine(LabelExpressionEngine.Arcade);
                labelClass.SetExpression(expression);
            }
            var whereClause = BridgeSupport.String(payload, "where_clause");
            if (whereClause is not null)
            {
                labelClass.SetWhereClause(whereClause);
            }
            if (payload.TryGetProperty("text_symbol", out var textSymbol)
                && textSymbol.ValueKind == JsonValueKind.Object)
            {
                labelClass.SetTextSymbol(BridgeSupport.TextSymbol(textSymbol));
            }
            labelClass.SetLabelVisibility(visible);
            layer.SetLabelVisibility(visible);
            return (object)new
            {
                map = map.Name,
                layer = layer.Name,
                visible,
                expression = labelClass.Expression,
            };
        });
    }

    internal static async Task<object> ListLabelClassesAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var includeCim = BridgeSupport.Bool(payload, "include_cim", false);
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = BridgeSupport.ResolveLayer(map, layerName) as FeatureLayer
                ?? throw new InvalidOperationException($"Feature layer not found: {layerName}");
            var definition = (CIMFeatureLayer)layer.GetDefinition();
            var cimByName = (definition.LabelClasses ?? Array.Empty<CIMLabelClass>())
                .ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
            return (object)new
            {
                map = map.Name,
                layer = layer.Name,
                layer_labels_visible = layer.IsLabelVisible,
                label_classes = layer.LabelClasses.Select(item =>
                {
                    cimByName.TryGetValue(item.Name, out var cim);
                    return new
                    {
                        name = item.Name,
                        visible = item.Visibility,
                        expression = item.Expression,
                        expression_engine = item.ExpressionEngine.ToString(),
                        where_clause = item.WhereClause,
                        priority = item.Priority,
                        minimum_scale = item.MinimumScale,
                        maximum_scale = item.MaximumScale,
                        use_coded_value = item.UseCodedValue,
                        placement_type = cim?.MaplexLabelPlacementProperties is not null
                            ? "Maplex"
                            : cim?.StandardLabelPlacementProperties is not null ? "Standard" : null,
                        cim_json = includeCim ? cim?.ToJson() : null,
                    };
                }).ToArray(),
            };
        });
    }

    internal static async Task<object> ConfigureLabelClassAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var className = BridgeSupport.String(payload, "label_class_name")
            ?? throw new ArgumentException("label_class_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = BridgeSupport.ResolveLayer(map, layerName) as FeatureLayer
                ?? throw new InvalidOperationException($"Feature layer not found: {layerName}");
            var labelClass = layer.LabelClasses.FirstOrDefault(item => string.Equals(
                item.Name, className, StringComparison.OrdinalIgnoreCase));
            var created = false;
            if (labelClass is null)
            {
                if (!BridgeSupport.Bool(payload, "create_if_missing", true))
                {
                    throw new InvalidOperationException($"Label class not found: {className}");
                }
                layer.AddLabelClass(className);
                labelClass = layer.LabelClasses.First(item => string.Equals(
                    item.Name, className, StringComparison.OrdinalIgnoreCase));
                created = true;
            }

            var newName = BridgeSupport.String(payload, "new_name");
            if (!string.IsNullOrWhiteSpace(newName))
            {
                if (layer.LabelClasses.Any(item => item != labelClass && string.Equals(
                    item.Name, newName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"Label class already exists: {newName}");
                }
                labelClass.SetName(newName);
            }
            var expression = BridgeSupport.String(payload, "expression");
            var field = BridgeSupport.String(payload, "field");
            if (string.IsNullOrWhiteSpace(expression) && !string.IsNullOrWhiteSpace(field))
            {
                expression = $"$feature.{field}";
            }
            if (expression is not null)
            {
                var engine = Enum.TryParse<LabelExpressionEngine>(
                    BridgeSupport.String(payload, "expression_engine", "Arcade"), true, out var parsedEngine)
                    ? parsedEngine
                    : LabelExpressionEngine.Arcade;
                labelClass.SetExpressionEngine(engine);
                labelClass.SetExpression(expression);
            }
            if (payload.TryGetProperty("where_clause", out _))
            {
                labelClass.SetWhereClause(BridgeSupport.String(payload, "where_clause", string.Empty)!);
            }
            if (payload.TryGetProperty("visible", out _))
            {
                labelClass.SetLabelVisibility(BridgeSupport.Bool(payload, "visible", true));
            }
            if (payload.TryGetProperty("priority", out _))
            {
                labelClass.SetPriority(Math.Clamp(BridgeSupport.Int(payload, "priority", 0), 0, 1000));
            }
            if (payload.TryGetProperty("minimum_scale", out _))
            {
                labelClass.SetMinimumScale(Math.Max(0, BridgeSupport.Double(payload, "minimum_scale")));
            }
            if (payload.TryGetProperty("maximum_scale", out _))
            {
                labelClass.SetMaximumScale(Math.Max(0, BridgeSupport.Double(payload, "maximum_scale")));
            }
            if (payload.TryGetProperty("use_coded_value", out _))
            {
                labelClass.SetUseCodedValue(BridgeSupport.Bool(payload, "use_coded_value", false));
            }
            if (payload.TryGetProperty("text_symbol", out var textSymbol)
                && textSymbol.ValueKind == JsonValueKind.Object)
            {
                labelClass.SetTextSymbol(BridgeSupport.TextSymbol(textSymbol));
            }

            var placementStyle = ResolveLabelPlacementStyle(payload);
            if (placementStyle?.LabelPlacement is CIMMaplexLabelPlacementProperties maplexStyle)
            {
                labelClass.SetMaplexLabelPlacementProperties(
                    maplexStyle.Clone() as CIMMaplexLabelPlacementProperties ?? maplexStyle);
            }
            else if (placementStyle?.LabelPlacement is CIMStandardLabelPlacementProperties standardStyle)
            {
                labelClass.SetStandardLabelPlacementProperties(
                    standardStyle.Clone() as CIMStandardLabelPlacementProperties ?? standardStyle);
            }
            if (payload.TryGetProperty("standard_placement", out var standardConfig)
                && standardConfig.ValueKind == JsonValueKind.Object)
            {
                var properties = labelClass.GetStandardLabelPlacementProperties()
                    ?? new CIMStandardLabelPlacementProperties();
                ApplyStandardPlacement(properties, standardConfig);
                labelClass.SetStandardLabelPlacementProperties(properties);
            }
            if (payload.TryGetProperty("maplex_placement", out var maplexConfig)
                && maplexConfig.ValueKind == JsonValueKind.Object)
            {
                var properties = labelClass.GetMaplexLabelPlacementProperties()
                    ?? new CIMMaplexLabelPlacementProperties();
                ApplyMaplexPlacement(properties, maplexConfig);
                labelClass.SetMaplexLabelPlacementProperties(properties);
            }
            if (payload.TryGetProperty("layer_labels_visible", out _))
            {
                layer.SetLabelVisibility(BridgeSupport.Bool(payload, "layer_labels_visible", true));
            }
            else if (labelClass.Visibility)
            {
                layer.SetLabelVisibility(true);
            }
            return (object)new
            {
                map = map.Name,
                layer = layer.Name,
                label_class = labelClass.Name,
                created,
                visible = labelClass.Visibility,
                expression = labelClass.Expression,
                expression_engine = labelClass.ExpressionEngine.ToString(),
                where_clause = labelClass.WhereClause,
                priority = labelClass.Priority,
                minimum_scale = labelClass.MinimumScale,
                maximum_scale = labelClass.MaximumScale,
                placement_style = placementStyle?.Name,
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> RemoveLabelClassesAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var names = BridgeSupport.Strings(payload, "label_class_names");
        if (names.Length == 0)
        {
            throw new ArgumentException("label_class_names must contain at least one name");
        }
        if (!BridgeSupport.Bool(payload, "confirm_remove", false))
        {
            throw new ArgumentException("confirm_remove=true is required to remove label classes");
        }
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = BridgeSupport.ResolveLayer(map, layerName) as FeatureLayer
                ?? throw new InvalidOperationException($"Feature layer not found: {layerName}");
            var before = layer.LabelClasses.Select(item => item.Name).ToArray();
            foreach (var name in names)
            {
                if (layer.LabelClasses.Any(item => string.Equals(
                    item.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    layer.RemoveLabelClass(name);
                }
            }
            var remaining = layer.LabelClasses.Select(item => item.Name).ToArray();
            return (object)new
            {
                map = map.Name,
                layer = layer.Name,
                requested = names,
                removed = before.Length - remaining.Length,
                remaining,
                source_data_modified = false,
            };
        });
    }

    private static LabelPlacementStyleItem? ResolveLabelPlacementStyle(JsonElement payload)
    {
        var itemName = BridgeSupport.String(payload, "placement_style_item_name");
        var search = BridgeSupport.String(payload, "placement_style_search", itemName);
        if (string.IsNullOrWhiteSpace(itemName) && string.IsNullOrWhiteSpace(search))
        {
            return null;
        }
        var styleType = (BridgeSupport.String(payload, "placement_engine", "maplex") ?? "maplex")
            .Equals("standard", StringComparison.OrdinalIgnoreCase)
            ? StyleItemType.StandardLabelPlacement
            : StyleItemType.MaplexLabelPlacement;
        var styleName = BridgeSupport.String(payload, "style_name");
        var candidates = Project.Current.GetItems<StyleProjectItem>()
            .Where(style => string.IsNullOrWhiteSpace(styleName)
                || string.Equals(style.Name, styleName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(style => StyleHelper.SearchLabelPlacements(
                style, styleType, search ?? string.Empty))
            .ToArray();
        return !string.IsNullOrWhiteSpace(itemName)
            ? candidates.FirstOrDefault(item => string.Equals(
                item.Name, itemName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Label-placement style not found: {itemName}")
            : candidates.FirstOrDefault()
                ?? throw new InvalidOperationException($"No label-placement style matched: {search}");
    }

    private static void ApplyStandardPlacement(
        CIMStandardLabelPlacementProperties properties,
        JsonElement config)
    {
        if (config.TryGetProperty("allow_overlapping_labels", out _))
            properties.AllowOverlappingLabels = BridgeSupport.Bool(config, "allow_overlapping_labels", false);
        if (config.TryGetProperty("place_only_inside_polygon", out _))
            properties.PlaceOnlyInsidePolygon = BridgeSupport.Bool(config, "place_only_inside_polygon", false);
        if (config.TryGetProperty("line_offset", out _))
            properties.LineOffset = BridgeSupport.Double(config, "line_offset");
        if (config.TryGetProperty("buffer_ratio", out _))
            properties.BufferRatio = BridgeSupport.Double(config, "buffer_ratio");
        if (config.TryGetProperty("rotation_field", out _))
            properties.RotationField = BridgeSupport.String(config, "rotation_field", string.Empty);
        if (Enum.TryParse<StandardPointPlacementMethod>(
            BridgeSupport.String(config, "point_placement_method"), true, out var point))
            properties.PointPlacementMethod = point;
        if (Enum.TryParse<StandardPolygonPlacementMethod>(
            BridgeSupport.String(config, "polygon_placement_method"), true, out var polygon))
            properties.PolygonPlacementMethod = polygon;
        if (Enum.TryParse<StandardNumLabelsOption>(
            BridgeSupport.String(config, "number_of_labels"), true, out var number))
            properties.NumLabelsOption = number;
    }

    private static void ApplyMaplexPlacement(
        CIMMaplexLabelPlacementProperties properties,
        JsonElement config)
    {
        if (config.TryGetProperty("primary_offset", out _))
            properties.PrimaryOffset = BridgeSupport.Double(config, "primary_offset");
        if (config.TryGetProperty("repeat_label", out _))
            properties.RepeatLabel = BridgeSupport.Bool(config, "repeat_label", false);
        if (config.TryGetProperty("minimum_repetition_interval", out _))
            properties.MinimumRepetitionInterval = BridgeSupport.Double(config, "minimum_repetition_interval");
        if (config.TryGetProperty("can_place_label_outside_polygon", out _))
            properties.CanPlaceLabelOutsidePolygon = BridgeSupport.Bool(config, "can_place_label_outside_polygon", false);
        if (config.TryGetProperty("prefer_horizontal_placement", out _))
            properties.PreferHorizontalPlacement = BridgeSupport.Bool(config, "prefer_horizontal_placement", false);
        if (config.TryGetProperty("never_remove_label", out _))
            properties.NeverRemoveLabel = BridgeSupport.Bool(config, "never_remove_label", false);
        if (config.TryGetProperty("label_buffer", out _))
            properties.LabelBuffer = Math.Clamp(BridgeSupport.Int(config, "label_buffer", 0), 0, 1000);
        if (config.TryGetProperty("feature_weight", out _))
            properties.FeatureWeight = Math.Clamp(BridgeSupport.Int(config, "feature_weight", 0), 0, 1000);
        if (Enum.TryParse<MaplexPointPlacementMethod>(
            BridgeSupport.String(config, "point_placement_method"), true, out var point))
            properties.PointPlacementMethod = point;
        if (Enum.TryParse<MaplexLinePlacementMethod>(
            BridgeSupport.String(config, "line_placement_method"), true, out var line))
            properties.LinePlacementMethod = line;
        if (Enum.TryParse<MaplexPolygonPlacementMethod>(
            BridgeSupport.String(config, "polygon_placement_method"), true, out var polygon))
            properties.PolygonPlacementMethod = polygon;
    }

    internal static async Task<object> AddStandaloneTableAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var path = BridgeSupport.String(payload, "table_path")
            ?? throw new ArgumentException("table_path is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(() =>
        {
            if (!DatasetPathExists(path))
            {
                throw new FileNotFoundException("Table dataset path was not found.", path);
            }
            var map = BridgeSupport.ResolveMap(requestedMap);
            var index = Math.Clamp(
                BridgeSupport.Int(payload, "index", map.StandaloneTables.Count),
                0,
                map.StandaloneTables.Count);
            var table = StandaloneTableFactory.Instance.CreateStandaloneTable(
                new Uri(path),
                map,
                index,
                BridgeSupport.String(payload, "table_name"));
            return (object)new
            {
                map = map.Name,
                table = table.Name,
                index,
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> ManageStandaloneTableAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var tableName = BridgeSupport.String(payload, "table_name")
            ?? throw new ArgumentException("table_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var table = ResolveStandaloneTable(map, tableName);
            var newName = BridgeSupport.String(payload, "new_name");
            if (!string.IsNullOrWhiteSpace(newName))
            {
                table.SetName(newName);
            }
            var definitionQuery = BridgeSupport.String(payload, "definition_query");
            if (definitionQuery is not null)
            {
                table.SetDefinitionQuery(definitionQuery);
            }
            if (payload.TryGetProperty("selectable", out var selectable)
                && selectable.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                table.SetSelectable(selectable.GetBoolean());
            }
            if (payload.TryGetProperty("show_popups", out var showPopups)
                && showPopups.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                table.SetShowPopups(showPopups.GetBoolean());
            }
            return (object)new
            {
                map = map.Name,
                table = table.Name,
                definition_query = table.DefinitionQuery,
                selectable = table.IsSelectable,
                show_popups = table.ShowPopups,
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> RemoveStandaloneTableAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var tableName = BridgeSupport.String(payload, "table_name")
            ?? throw new ArgumentException("table_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var table = ResolveStandaloneTable(map, tableName);
            map.RemoveStandaloneTable(table);
            return (object)new
            {
                map = map.Name,
                removed_from_map = tableName,
                source_data_deleted = false,
            };
        });
    }

    internal static async Task<object> ConfigurePopupAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = BridgeSupport.ResolveLayer(map, layerName);
            var showPopups = payload.TryGetProperty("show_popups", out var show)
                && show.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? show.GetBoolean()
                    : (bool?)null;
            var displayField = BridgeSupport.String(payload, "display_field");
            var importPath = BridgeSupport.String(payload, "import_popup_path");
            var exportPath = BridgeSupport.String(payload, "export_popup_path");

            if (!string.IsNullOrWhiteSpace(importPath) && !File.Exists(importPath))
            {
                throw new FileNotFoundException("Popup configuration file was not found.", importPath);
            }
            if (!string.IsNullOrWhiteSpace(exportPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(exportPath) ?? string.Empty);
            }

            if (layer is FeatureLayer featureLayer)
            {
                if (!string.IsNullOrWhiteSpace(importPath))
                {
                    featureLayer.ImportPopupConfiguration(importPath);
                }
                if (showPopups is not null)
                {
                    featureLayer.SetShowPopups(showPopups.Value);
                }
                if (!string.IsNullOrWhiteSpace(displayField))
                {
                    featureLayer.SetDisplayField(displayField);
                }
                if (!string.IsNullOrWhiteSpace(exportPath))
                {
                    featureLayer.ExportPopupConfiguration(exportPath);
                }
                return (object)new
                {
                    map = map.Name,
                    layer = featureLayer.Name,
                    show_popups = featureLayer.ShowPopups,
                    display_field = featureLayer.DisplayField,
                    imported = importPath,
                    exported = exportPath,
                    source_data_modified = false,
                };
            }
            if (layer is RasterLayer rasterLayer)
            {
                if (!string.IsNullOrWhiteSpace(importPath))
                {
                    rasterLayer.ImportPopupConfiguration(importPath);
                }
                if (showPopups is not null)
                {
                    rasterLayer.SetShowPopups(showPopups.Value);
                }
                if (!string.IsNullOrWhiteSpace(displayField))
                {
                    rasterLayer.SetDisplayField(displayField);
                }
                if (!string.IsNullOrWhiteSpace(exportPath))
                {
                    rasterLayer.ExportPopupConfiguration(exportPath);
                }
                return (object)new
                {
                    map = map.Name,
                    layer = rasterLayer.Name,
                    show_popups = rasterLayer.ShowPopups,
                    display_field = rasterLayer.DisplayField,
                    imported = importPath,
                    exported = exportPath,
                    source_data_modified = false,
                };
            }
            throw new InvalidOperationException(
                $"Popup configuration is supported for feature and raster layers: {layerName}");
        });
    }

    internal static async Task<object> ConfigureFieldsAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var layerName = BridgeSupport.String(payload, "layer_name");
        var tableName = BridgeSupport.String(payload, "table_name");
        if (string.IsNullOrWhiteSpace(layerName) == string.IsNullOrWhiteSpace(tableName))
        {
            throw new ArgumentException("Specify exactly one of layer_name or table_name");
        }
        if (!payload.TryGetProperty("fields", out var fields)
            || fields.ValueKind != JsonValueKind.Array
            || fields.GetArrayLength() == 0)
        {
            throw new ArgumentException("fields array is required");
        }
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            List<FieldDescription> descriptions;
            Action<List<FieldDescription>> apply;
            string memberName;
            if (!string.IsNullOrWhiteSpace(layerName))
            {
                var layer = BridgeSupport.ResolveLayer(map, layerName) as FeatureLayer
                    ?? throw new InvalidOperationException($"Feature layer not found: {layerName}");
                descriptions = layer.GetFieldDescriptions();
                apply = layer.SetFieldDescriptions;
                memberName = layer.Name;
            }
            else
            {
                var table = ResolveStandaloneTable(map, tableName!);
                descriptions = table.GetFieldDescriptions();
                apply = table.SetFieldDescriptions;
                memberName = table.Name;
            }

            foreach (var config in fields.EnumerateArray())
            {
                var name = BridgeSupport.String(config, "name")
                    ?? throw new ArgumentException("Each field configuration requires a name");
                var description = descriptions.FirstOrDefault(item => string.Equals(
                    item.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Field not found: {name}");
                var alias = BridgeSupport.String(config, "alias");
                if (alias is not null)
                {
                    description.Alias = alias;
                }
                if (config.TryGetProperty("visible", out var visible)
                    && visible.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    description.IsVisible = visible.GetBoolean();
                }
                if (config.TryGetProperty("highlighted", out var highlighted)
                    && highlighted.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    description.IsHighlighted = highlighted.GetBoolean();
                }
                if (config.TryGetProperty("read_only", out var readOnly)
                    && readOnly.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    description.IsReadOnly = readOnly.GetBoolean();
                }
            }
            apply(descriptions);
            return (object)new
            {
                map = map.Name,
                member = memberName,
                fields = descriptions.Select(description => new
                {
                    name = description.Name,
                    alias = description.Alias,
                    visible = description.IsVisible,
                    highlighted = description.IsHighlighted,
                    read_only = description.IsReadOnly,
                }).ToArray(),
                source_schema_modified = false,
            };
        });
    }

    internal static async Task<object> ConfigureDisplayFiltersAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = BridgeSupport.ResolveLayer(map, layerName) as FeatureLayer
                ?? throw new InvalidOperationException($"Feature layer not found: {layerName}");
            var definition = (CIMFeatureLayer)layer.GetDefinition();
            var configured = new List<CIMDisplayFilter>();
            if (payload.TryGetProperty("filters", out var filters)
                && filters.ValueKind == JsonValueKind.Array)
            {
                foreach (var filter in filters.EnumerateArray())
                {
                    configured.Add(new CIMDisplayFilter
                    {
                        Name = BridgeSupport.String(filter, "name")
                            ?? throw new ArgumentException("Each display filter requires a name"),
                        WhereClause = BridgeSupport.String(filter, "where_clause", "1=1"),
                        MinScale = BridgeSupport.Double(filter, "min_scale", 0),
                        MaxScale = BridgeSupport.Double(filter, "max_scale", 0),
                    });
                }
                definition.DisplayFilters = configured.ToArray();
                definition.DisplayFilterChoices = configured.Select(item => item.Clone()).ToArray();
            }
            definition.EnableDisplayFilters = BridgeSupport.Bool(
                payload, "enabled", configured.Count > 0 || definition.EnableDisplayFilters);
            var activeName = BridgeSupport.String(payload, "active_filter_name");
            if (activeName is not null)
            {
                definition.DisplayFilterName = activeName;
            }
            var mode = BridgeSupport.String(payload, "filter_type");
            if (!string.IsNullOrWhiteSpace(mode))
            {
                if (!Enum.TryParse<DisplayFilterType>(mode, true, out var parsed))
                {
                    throw new ArgumentException($"Unsupported display filter type: {mode}");
                }
                definition.DisplayFiltersType = parsed;
            }
            layer.SetDefinition(definition);
            return (object)new
            {
                map = map.Name,
                layer = layer.Name,
                enabled = definition.EnableDisplayFilters,
                active_filter_name = definition.DisplayFilterName,
                filter_type = definition.DisplayFiltersType.ToString(),
                filters = (definition.DisplayFilters ?? Array.Empty<CIMDisplayFilter>()).Select(item => new
                {
                    name = item.Name,
                    where_clause = item.WhereClause,
                    min_scale = item.MinScale,
                    max_scale = item.MaxScale,
                }).ToArray(),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> ConfigureTimeAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = BridgeSupport.ResolveLayer(map, layerName) as FeatureLayer
                ?? throw new InvalidOperationException($"Feature layer not found: {layerName}");
            if (BridgeSupport.Bool(payload, "clear", false))
            {
                if (!layer.CanClearTime())
                {
                    throw new InvalidOperationException($"Layer time cannot be cleared: {layerName}");
                }
                layer.ClearTime();
                return (object)new { map = map.Name, layer = layer.Name, cleared = true };
            }
            var parameters = new TimeParameters
            {
                StartTimeFieldName = BridgeSupport.String(payload, "start_time_field"),
                EndTimeFieldName = BridgeSupport.String(payload, "end_time_field"),
                IsCumulative = BridgeSupport.Bool(payload, "cumulative", false),
                TimeFormat = BridgeSupport.String(payload, "time_format"),
                IsLiveFeed = BridgeSupport.Bool(payload, "live_feed", false),
            };
            if (!parameters.IsValid())
            {
                throw new ArgumentException("The supplied time parameters are not valid for ArcGIS Pro.");
            }
            if (!layer.CanSetTime(parameters))
            {
                throw new InvalidOperationException($"Layer does not support the supplied time parameters: {layerName}");
            }
            layer.SetTime(parameters);
            return (object)new
            {
                map = map.Name,
                layer = layer.Name,
                start_time_field = parameters.StartTimeFieldName,
                end_time_field = parameters.EndTimeFieldName,
                cumulative = parameters.IsCumulative,
                live_feed = parameters.IsLiveFeed,
                time_format = parameters.TimeFormat,
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> GetChartsAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = BridgeSupport.ResolveLayer(map, layerName) as FeatureLayer
                ?? throw new InvalidOperationException($"Feature layer not found: {layerName}");
            var definition = (CIMFeatureLayer)layer.GetDefinition();
            return (object)new
            {
                map = map.Name,
                layer = layer.Name,
                charts = (definition.Charts ?? Array.Empty<CIMChart>()).Select(chart => new
                {
                    name = chart.Name,
                    chart_type = chart.ChartType.ToString(),
                    cim_json = chart.ToJson(),
                }).ToArray(),
            };
        });
    }

    internal static async Task<object> SetChartsAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        if (!payload.TryGetProperty("charts", out var charts)
            || charts.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("charts array is required");
        }
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = BridgeSupport.ResolveLayer(map, layerName) as FeatureLayer
                ?? throw new InvalidOperationException($"Feature layer not found: {layerName}");
            var parsed = charts.EnumerateArray()
                .Select(chart => CIMChart.FromJson(
                    chart.GetRawText(), new JsonDeserializationSettings()))
                .ToArray();
            var definition = (CIMFeatureLayer)layer.GetDefinition();
            definition.Charts = parsed;
            layer.SetDefinition(definition);
            return (object)new
            {
                map = map.Name,
                layer = layer.Name,
                chart_count = parsed.Length,
                charts = parsed.Select(chart => new
                {
                    name = chart.Name,
                    chart_type = chart.ChartType.ToString(),
                }).ToArray(),
                source_data_modified = false,
            };
        });
    }

    private static StandaloneTable ResolveStandaloneTable(Map map, string tableName)
    {
        return map.GetStandaloneTablesAsFlattenedList()
            .FirstOrDefault(table => string.Equals(
                table.Name, tableName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Standalone table not found: {tableName}");
    }

    private static bool DatasetPathExists(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            return true;
        }
        foreach (var suffix in new[] { ".gdb", ".sde", ".mdb" })
        {
            var index = path.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var container = path[..(index + suffix.Length)];
                return Directory.Exists(container) || File.Exists(container);
            }
        }
        return false;
    }
}
