using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Data.Analyst3D;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using ArcGIS.Desktop.Mapping.Voxel;

namespace ArcGISCodexBridgeAddin;

internal static class SpecializedLayerActions
{
    internal static async Task<object> InspectLasAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var layerName = Required(payload, "layer_name");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = ResolveLayer<LasDatasetLayer>(map, layerName, "LAS dataset");
            return DescribeLas(map, layer);
        });
    }

    internal static async Task<object> ConfigureLasAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var layerName = Required(payload, "layer_name");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = ResolveLayer<LasDatasetLayer>(map, layerName, "LAS dataset");
            if (payload.TryGetProperty("eye_dome_lighting_enabled", out _))
                layer.SetEyeDomeLightingEnabled(BridgeSupport.Bool(payload, "eye_dome_lighting_enabled"));
            if (payload.TryGetProperty("eye_dome_lighting_strength", out _))
                layer.SetEyeDomeLightingStrength(Math.Clamp(
                    BridgeSupport.Double(payload, "eye_dome_lighting_strength"), 0, 100));
            if (payload.TryGetProperty("eye_dome_lighting_radius", out _))
                layer.SetEyeDomeLightingRadius(Math.Max(
                    0, BridgeSupport.Double(payload, "eye_dome_lighting_radius")));
            if (payload.TryGetProperty("selectable_class_codes", out _))
                layer.SetSelectableClassCodes(BridgeSupport.Ints(payload, "selectable_class_codes"));
            if (payload.TryGetProperty("select_visible_points", out _))
                layer.SetSelectVisiblePoints(BridgeSupport.Bool(payload, "select_visible_points"));
            if (payload.TryGetProperty("active_surface_constraints", out _))
                layer.SetActiveSurfaceConstraints(BridgeSupport.Strings(
                    payload, "active_surface_constraints").ToList());
            if (BridgeSupport.Bool(payload, "clear_selection"))
                layer.ClearSelection();

            if (payload.TryGetProperty("display_filter_type", out _))
            {
                var raw = BridgeSupport.String(payload, "display_filter_type");
                if (!Enum.TryParse<LasPointDisplayFilterType>(raw, true, out var filterType))
                    throw new ArgumentException($"Unsupported display_filter_type: {raw}");
                layer.SetDisplayFilter(filterType);
            }
            else if (payload.TryGetProperty("display_filter", out var filterConfig)
                && filterConfig.ValueKind == JsonValueKind.Object)
            {
                var filter = new LasPointDisplayFilter
                {
                    ClassCodes = BridgeSupport.Ints(filterConfig, "class_codes").ToList(),
                    SurfaceConstraints = BridgeSupport.Strings(
                        filterConfig, "surface_constraints").ToList(),
                    Returns = ParseEnums<LasReturnType>(filterConfig, "returns"),
                    KeyPoints = BridgeSupport.Bool(filterConfig, "key_points"),
                    SyntheticPoints = BridgeSupport.Bool(filterConfig, "synthetic_points"),
                    WithheldPoints = BridgeSupport.Bool(filterConfig, "withheld_points"),
                    OverlapPoints = BridgeSupport.Bool(filterConfig, "overlap_points"),
                    NotFlagged = BridgeSupport.Bool(filterConfig, "not_flagged", true),
                };
                layer.SetDisplayFilter(filter);
            }
            return DescribeLas(map, layer);
        });
    }

    internal static async Task<object> InspectVoxelAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var layerName = Required(payload, "layer_name");
        var distributionLimit = Math.Clamp(
            BridgeSupport.Int(payload, "distribution_limit", 256), 0, 4096);
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = ResolveLayer<VoxelLayer>(map, layerName, "voxel");
            return DescribeVoxel(map, layer, distributionLimit);
        });
    }

    internal static async Task<object> ConfigureVoxelAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var layerName = Required(payload, "layer_name");
        var distributionLimit = Math.Clamp(
            BridgeSupport.Int(payload, "distribution_limit", 256), 0, 4096);
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = ResolveLayer<VoxelLayer>(map, layerName, "voxel");
            if (payload.TryGetProperty("vertical_exaggeration", out _))
                layer.SetVerticalExaggeration(Math.Max(
                    0, BridgeSupport.Double(payload, "vertical_exaggeration", 1)));
            if (payload.TryGetProperty("cartographic_offset", out _))
                layer.SetCartographicOffset(BridgeSupport.Double(payload, "cartographic_offset"));
            if (payload.TryGetProperty("snappable", out _))
                layer.SetSnappable(BridgeSupport.Bool(payload, "snappable"));
            if (payload.TryGetProperty("diffuse_lighting_enabled", out _))
                layer.SetDiffuseLightingEnabled(BridgeSupport.Bool(payload, "diffuse_lighting_enabled"));
            if (payload.TryGetProperty("diffuse_lighting", out _))
                layer.SetDiffuseLighting(Math.Clamp(
                    BridgeSupport.Double(payload, "diffuse_lighting"), 0, 1));
            if (payload.TryGetProperty("specular_lighting_enabled", out _))
                layer.SetSpecularLightingEnabled(BridgeSupport.Bool(payload, "specular_lighting_enabled"));
            if (payload.TryGetProperty("specular_lighting", out _))
                layer.SetSpecularLighting(Math.Clamp(
                    BridgeSupport.Double(payload, "specular_lighting"), 0, 1));
            if (payload.TryGetProperty("visualization", out _))
            {
                var raw = BridgeSupport.String(payload, "visualization");
                if (!Enum.TryParse<VoxelVisualization>(raw, true, out var visualization))
                    throw new ArgumentException($"Unsupported voxel visualization: {raw}");
                layer.SetVisualization(visualization);
            }
            ApplyVoxelContainers(layer, payload);

            var variableName = BridgeSupport.String(payload, "selected_variable");
            if (!string.IsNullOrWhiteSpace(variableName))
            {
                layer.SetSelectedVariableProfile(layer.GetVariableProfile(variableName)
                    ?? throw new InvalidOperationException($"Voxel variable not found: {variableName}"));
            }
            if (payload.TryGetProperty("renderer", out var rendererConfig)
                && rendererConfig.ValueKind == JsonValueKind.Object)
            {
                var profile = ResolveVoxelProfile(layer, payload);
                var rendererType = Required(rendererConfig, "cim_type");
                var rendererJson = Required(rendererConfig, "cim_json");
                profile.SetRenderer(CimObjectActions.ParseNamedType<CIMVoxelRenderer>(
                    rendererType, rendererJson));
            }
            if (payload.TryGetProperty("isosurfaces", out var isosurfaces)
                && isosurfaces.ValueKind == JsonValueKind.Array)
            {
                var profile = ResolveVoxelProfile(layer, payload);
                ApplyIsosurfaces(
                    profile,
                    isosurfaces,
                    BridgeSupport.Bool(payload, "confirm_remove_isosurfaces"));
            }
            return DescribeVoxel(map, layer, distributionLimit);
        });
    }

    private static object DescribeLas(Map map, LasDatasetLayer layer)
    {
        var display = layer.GetDisplayFilter();
        using var dataset = layer.GetLasDataset();
        var fileCounts = dataset.GetFileCounts();
        var version = dataset.GetVersion();
        var extent = dataset.GetExtent();
        return new
        {
            map = map.Name,
            layer = layer.Name,
            eye_dome_lighting_enabled = layer.IsEyeDomeLightingEnabled,
            eye_dome_lighting_strength = layer.EyeDomeLightingStrength,
            eye_dome_lighting_radius = layer.EyeDomeLightingRadius,
            has_selection = layer.HasSelection,
            selection_count = layer.SelectionCount,
            selectable_class_codes = layer.GetSelectableClassCodes(),
            select_visible_points = layer.GetSelectVisiblePoints(),
            active_surface_constraints = layer.GetActiveSurfaceConstraints().Select(item => new
            {
                data_source_name = item.DataSourceName,
                workspace_path = item.WorkspacePath,
                height_field = item.HeightField,
                tag_field = item.TagField,
                surface_type = item.SurfaceType.ToString(),
            }).ToArray(),
            display_filter = display is null ? null : new
            {
                class_codes = display.ClassCodes ?? new List<int>(),
                returns = display.Returns?.Select(item => item.ToString()).ToArray() ?? Array.Empty<string>(),
                surface_constraints = display.SurfaceConstraints ?? new List<string>(),
                key_points = display.KeyPoints,
                synthetic_points = display.SyntheticPoints,
                withheld_points = display.WithheldPoints,
                overlap_points = display.OverlapPoints,
                not_flagged = display.NotFlagged,
            },
            dataset = new
            {
                name = dataset.GetName(),
                point_count = dataset.GetPointCount(),
                size_bytes = dataset.GetSizeInBytes(),
                uncompressed_size_bytes = dataset.GetUncompressedSizeInBytes(),
                file_count = fileCounts.Item1,
                missing_file_count = fileCounts.Item2,
                version = new[] { version.Item1, version.Item2, version.Item3 },
                surface_constraint_count = dataset.GetSurfaceConstraintCount(),
                unique_class_codes = dataset.GetUniqueClassCodes(),
                unique_returns = dataset.GetUniqueReturns().Select(item => item.ToString()).ToArray(),
                extent = new
                {
                    xmin = extent.XMin,
                    ymin = extent.YMin,
                    xmax = extent.XMax,
                    ymax = extent.YMax,
                    zmin = extent.ZMin,
                    zmax = extent.ZMax,
                    spatial_reference = extent.SpatialReference?.Name,
                },
            },
            source_data_modified = false,
        };
    }

    private static object DescribeVoxel(Map map, VoxelLayer layer, int distributionLimit)
    {
        var profiles = layer.GetVariableProfiles().Select(profile =>
        {
            var stats = profile.Statistics;
            var distribution = stats?.Distribution ?? Array.Empty<double>();
            return new
            {
                variable = profile.Variable,
                description = profile.Description,
                data_type = profile.DataType.ToString(),
                can_create_isosurface = profile.CanCreateIsosurface,
                maximum_isosurface_count = profile.MaxNumberOfIsosurfaces,
                renderer_type = profile.Renderer?.GetType().Name,
                renderer_cim_json = profile.Renderer?.ToJson(),
                statistics = stats is null ? null : new
                {
                    minimum = stats.MinimumValue,
                    maximum = stats.MaximumValue,
                    mean = stats.Mean,
                    standard_deviation = stats.StandardDeviation,
                    distribution_minimum = stats.DistributionMinimumValue,
                    distribution_maximum = stats.DistributionMaximumValue,
                    distribution = distribution.Take(distributionLimit).ToArray(),
                    distribution_count = distribution.Length,
                    distribution_truncated = distribution.Length > distributionLimit,
                    most_frequent_values = stats.MostFrequentValues ?? Array.Empty<int>(),
                    most_frequent_value_counts = stats.MostFrequentValuesCounts ?? Array.Empty<long>(),
                },
                isosurfaces = profile.GetIsosurfaces().Select(item => new
                {
                    id = item.ID,
                    name = item.Name,
                    value = item.Value,
                    visible = item.IsVisible,
                    custom_color = item.IsCustomColor,
                    color_cim_json = item.Color?.ToJson(),
                }).ToArray(),
            };
        }).ToArray();
        var volumes = layer.GetVolumes().Select((volume, index) =>
        {
            var size = volume.GetVolumeSize();
            return new
            {
                index,
                size = new { x = size.Item1, y = size.Item2, z = size.Item3 },
                variables = volume.GetVariableProfiles().Select(item => item.Variable).ToArray(),
                sections = volume.GetSections().Select(DescribeSection).ToArray(),
                slices = volume.GetSlices().Select(DescribeSlice).ToArray(),
            };
        }).ToArray();
        return new
        {
            map = map.Name,
            layer = layer.Name,
            data_source_type = layer.GetDataSourceType().ToString(),
            visualization = layer.Visualization.ToString(),
            selected_variable = layer.SelectedVariableProfile?.Variable,
            selected_volume_index = layer.SelectedVolume is null
                ? (int?)null
                : layer.GetVolumes().IndexOf(layer.SelectedVolume),
            vertical_exaggeration = layer.VerticalExaggeration,
            cartographic_offset = layer.CartographicOffset,
            snappable = layer.IsSnappable,
            diffuse_lighting_enabled = layer.IsDiffuseLightingEnabled,
            diffuse_lighting = layer.DiffuseLighting,
            specular_lighting_enabled = layer.IsSpecularLightingEnabled,
            specular_lighting = layer.SpecularLighting,
            containers = new
            {
                surface_expanded = layer.IsSurfaceContainerExpanded,
                isosurface_visible = layer.IsIsosurfaceContainerVisible,
                isosurface_expanded = layer.IsIsosurfaceContainerExpanded,
                section_visible = layer.IsSectionContainerVisible,
                section_expanded = layer.IsSectionContainerExpanded,
                locked_section_visible = layer.IsLockedSectionContainerVisible,
                locked_section_expanded = layer.IsLockedSectionContainerExpanded,
                slice_visible = layer.IsSliceContainerVisible,
                slice_expanded = layer.IsSliceContainerExpanded,
            },
            unique_times = layer.GetUniqueTimes().Take(1000).Select(item => item.ToString("O")).ToArray(),
            variables = profiles,
            volumes,
            locked_sections = layer.GetLockedSections().Select(item => new
            {
                id = item.ID,
                name = item.Name,
                variable = item.VariableName,
                visible = item.IsVisible,
                expanded = item.IsExpanded,
                position = Coordinate(item.VoxelPosition),
                normal = Coordinate(item.Normal),
            }).ToArray(),
            source_data_modified = false,
        };
    }

    private static object DescribeSection(SectionDefinition item) => new
    {
        id = item.ID,
        name = item.Name,
        visible = item.IsVisible,
        position = Coordinate(item.VoxelPosition),
        normal = Coordinate(item.Normal),
    };

    private static object DescribeSlice(SliceDefinition item) => new
    {
        name = item.Name,
        visible = item.IsVisible,
        position = Coordinate(item.VoxelPosition),
        normal = Coordinate(item.Normal),
    };

    private static object Coordinate(ArcGIS.Core.Geometry.Coordinate3D value) => new
    {
        x = value.X,
        y = value.Y,
        z = value.Z,
    };

    private static void ApplyVoxelContainers(VoxelLayer layer, JsonElement payload)
    {
        if (payload.TryGetProperty("surface_container_expanded", out _))
            layer.SetSurfaceContainerExpanded(BridgeSupport.Bool(payload, "surface_container_expanded"));
        if (payload.TryGetProperty("isosurface_container_visible", out _))
            layer.SetIsosurfaceContainerVisibility(BridgeSupport.Bool(payload, "isosurface_container_visible"));
        if (payload.TryGetProperty("isosurface_container_expanded", out _))
            layer.SetIsosurfaceContainerExpanded(BridgeSupport.Bool(payload, "isosurface_container_expanded"));
        if (payload.TryGetProperty("section_container_visible", out _))
            layer.SetSectionContainerVisibility(BridgeSupport.Bool(payload, "section_container_visible"));
        if (payload.TryGetProperty("section_container_expanded", out _))
            layer.SetSectionContainerExpanded(BridgeSupport.Bool(payload, "section_container_expanded"));
        if (payload.TryGetProperty("locked_section_container_visible", out _))
            layer.SetLockedSectionContainerVisibility(BridgeSupport.Bool(payload, "locked_section_container_visible"));
        if (payload.TryGetProperty("locked_section_container_expanded", out _))
            layer.SetLockedSectionContainerExpanded(BridgeSupport.Bool(payload, "locked_section_container_expanded"));
        if (payload.TryGetProperty("slice_container_visible", out _))
            layer.SetSliceContainerVisibility(BridgeSupport.Bool(payload, "slice_container_visible"));
        if (payload.TryGetProperty("slice_container_expanded", out _))
            layer.SetSliceContainerExpanded(BridgeSupport.Bool(payload, "slice_container_expanded"));
    }

    private static VoxelVariableProfile ResolveVoxelProfile(VoxelLayer layer, JsonElement payload)
    {
        var variable = BridgeSupport.String(payload, "variable_name")
            ?? BridgeSupport.String(payload, "selected_variable")
            ?? layer.SelectedVariableProfile?.Variable;
        if (string.IsNullOrWhiteSpace(variable))
            throw new InvalidOperationException("variable_name is required when the voxel layer has no selected variable");
        return layer.GetVariableProfile(variable)
            ?? throw new InvalidOperationException($"Voxel variable not found: {variable}");
    }

    private static void ApplyIsosurfaces(
        VoxelVariableProfile profile,
        JsonElement commands,
        bool confirmRemove)
    {
        foreach (var item in commands.EnumerateArray())
        {
            var operation = BridgeSupport.String(item, "operation", "create")!.ToLowerInvariant();
            var name = BridgeSupport.String(item, "name");
            var id = BridgeSupport.String(item, "id");
            var existing = profile.GetIsosurfaces().FirstOrDefault(candidate =>
                (!string.IsNullOrWhiteSpace(id) && string.Equals(candidate.ID, id, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(name) && string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)));
            if (operation == "create")
            {
                var created = new IsosurfaceDefinition
                {
                    Name = name ?? throw new ArgumentException("Isosurface create requires name"),
                    Value = OptionalDouble(item, "value"),
                    IsVisible = BridgeSupport.Bool(item, "visible", true),
                    IsCustomColor = item.TryGetProperty("color", out _),
                };
                if (created.IsCustomColor)
                    created.Color = BridgeSupport.Color(item, "color");
                profile.CreateIsosurface(created);
            }
            else if (operation == "update")
            {
                if (existing is null)
                    throw new InvalidOperationException("Isosurface to update was not found");
                if (item.TryGetProperty("name", out _))
                    existing.Name = name!;
                if (item.TryGetProperty("value", out _))
                    existing.Value = OptionalDouble(item, "value");
                if (item.TryGetProperty("visible", out _))
                    existing.IsVisible = BridgeSupport.Bool(item, "visible");
                if (item.TryGetProperty("color", out _))
                {
                    existing.Color = BridgeSupport.Color(item, "color");
                    existing.IsCustomColor = true;
                }
                if (BridgeSupport.Bool(item, "use_automatic_color"))
                    existing.IsCustomColor = false;
                profile.UpdateIsosurface(existing);
            }
            else if (operation == "delete")
            {
                if (!confirmRemove)
                    throw new ArgumentException("confirm_remove_isosurfaces=true is required for delete");
                if (existing is null)
                    throw new InvalidOperationException("Isosurface to delete was not found");
                profile.DeleteIsosurface(existing);
            }
            else
            {
                throw new ArgumentException("Isosurface operation must be create, update, or delete");
            }
        }
    }

    private static double? OptionalDouble(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result))
            throw new ArgumentException($"{name} must be a number or null");
        return result;
    }

    private static List<T> ParseEnums<T>(JsonElement payload, string name) where T : struct, Enum
    {
        var result = new List<T>();
        foreach (var raw in BridgeSupport.Strings(payload, name))
        {
            if (!Enum.TryParse<T>(raw, true, out var parsed))
                throw new ArgumentException($"Unsupported {name} value: {raw}");
            result.Add(parsed);
        }
        return result;
    }

    private static T ResolveLayer<T>(Map map, string layerName, string label) where T : Layer
    {
        return map.GetLayersAsFlattenedList().OfType<T>().FirstOrDefault(layer => string.Equals(
            layer.Name, layerName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{label} layer not found: {layerName}");
    }

    private static string Required(JsonElement payload, string name)
    {
        return BridgeSupport.String(payload, name)
            ?? throw new ArgumentException($"{name} is required");
    }
}
