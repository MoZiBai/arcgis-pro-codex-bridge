using System.IO;
using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGIS.Desktop.Presentations;
using ArcGIS.Desktop.Reports;

namespace ArcGISCodexBridgeAddin;

internal static class ProjectActions
{
    internal static async Task<object> CreateBlankAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var output = BridgeSupport.String(payload, "output_aprx")
            ?? throw new ArgumentException("output_aprx is required");
        var outputPath = Path.GetFullPath(output);
        if (!outputPath.EndsWith(".aprx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("output_aprx must end in .aprx");
        }
        var overwrite = BridgeSupport.Bool(payload, "overwrite", false);
        if (Directory.Exists(outputPath))
        {
            throw new IOException($"output_aprx resolves to a directory, not a project file: {outputPath}");
        }
        if (File.Exists(outputPath) && !overwrite)
        {
            throw new IOException($"Output project already exists: {outputPath}");
        }

        var parent = Path.GetDirectoryName(outputPath)
            ?? throw new ArgumentException("output_aprx must include a valid parent folder");
        Directory.CreateDirectory(parent);
        var projectName = BridgeSupport.String(
            payload,
            "project_name",
            Path.GetFileNameWithoutExtension(outputPath));
        if (string.IsNullOrWhiteSpace(projectName))
        {
            throw new ArgumentException("project_name must not be empty");
        }
        projectName = projectName.Trim();
        if (!string.Equals(projectName, Path.GetFileName(projectName), StringComparison.Ordinal)
            || projectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("project_name must be a name, not a path");
        }

        // CreateAsync is the public ArcGIS Pro SDK entry point for a project
        // when Project.Current is null. ArcGIS Pro may create SDK bootstrap
        // artifacts beside the initial APRX, so create them in a dedicated
        // support folder. This also keeps the requested output path free for
        // SaveAsAsync and guarantees that output_aprx means an exact file path.
        var outputStem = Path.GetFileNameWithoutExtension(outputPath);
        var supportFolder = Path.Combine(parent, $"{outputStem}_files");
        if (Directory.Exists(supportFolder) || File.Exists(supportFolder))
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            supportFolder = Path.Combine(parent, $"{outputStem}_files_{suffix}");
        }
        Directory.CreateDirectory(supportFolder);

        var settings = new CreateProjectSettings
        {
            TemplateType = TemplateType.Untitled,
            LocationPath = supportFolder,
            Name = projectName,
            CreateNewProjectFolder = false,
        };
        var project = await await FrameworkApplication.Current.Dispatcher.InvokeAsync(
            () => Project.CreateAsync(settings));

        // ArcGIS Pro 3.6 completes some untitled-project initialization during
        // the first Save As. Perform that transition inside the support folder
        // so the caller's exact output path remains untouched. Once this call
        // returns, QueuedTask project-item creation and the final flat-file
        // SaveAs both use a fully materialized current project.
        var bootstrapOutputPath = Path.Combine(
            supportFolder,
            $"{projectName}-bootstrap.aprx");
        var bootstrapReady = await await FrameworkApplication.Current.Dispatcher.InvokeAsync(
            () => project.SaveAsAsync(bootstrapOutputPath, true));
        if (!bootstrapReady)
        {
            throw new IOException("ArcGIS Pro could not materialize the bootstrap project");
        }
        project = Project.Current ?? project;

        var requestedMap = BridgeSupport.String(payload, "map_name");
        var requestedLayout = BridgeSupport.String(payload, "layout_name");
        var createMap = BridgeSupport.Bool(payload, "create_map", !string.IsNullOrWhiteSpace(requestedMap));
        var createLayout = BridgeSupport.Bool(payload, "create_layout", !string.IsNullOrWhiteSpace(requestedLayout));
        var addMapFrame = BridgeSupport.Bool(payload, "create_map_frame", createLayout);
        var openMap = BridgeSupport.Bool(payload, "open_map", false);
        var openLayout = BridgeSupport.Bool(payload, "open_layout", false);
        Map? createdMap = null;
        Layout? createdLayout = null;
        MapFrame? createdMapFrame = null;

        var structure = await QueuedTask.Run<object>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Project.Current ?? project;
            if (createMap)
            {
                var mapName = requestedMap ?? "Map";
                if (current.GetItems<MapProjectItem>().Any(item =>
                    string.Equals(item.Name, mapName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"Map already exists: {mapName}");
                }
                var mapType = Enum.TryParse<MapType>(
                    BridgeSupport.String(payload, "map_type", "Map"), true, out var parsedMapType)
                    ? parsedMapType
                    : throw new ArgumentException("Unsupported map_type");
                var viewingMode = Enum.TryParse<MapViewingMode>(
                    BridgeSupport.String(
                        payload,
                        "viewing_mode",
                        mapType == MapType.Scene ? "SceneLocal" : "Map"),
                    true,
                    out var parsedViewingMode)
                    ? parsedViewingMode
                    : throw new ArgumentException("Unsupported viewing_mode");
                var basemap = Enum.TryParse<Basemap>(
                    BridgeSupport.String(payload, "basemap", "None"), true, out var parsedBasemap)
                    ? parsedBasemap
                    : throw new ArgumentException("Unsupported basemap");
                createdMap = MapFactory.Instance.CreateMap(mapName, mapType, viewingMode, basemap);
            }

            if (createLayout)
            {
                var layoutName = requestedLayout ?? "Layout";
                if (current.GetItems<LayoutProjectItem>().Any(item =>
                    string.Equals(item.Name, layoutName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"Layout already exists: {layoutName}");
                }
                var pageWidth = Math.Max(0.01, BridgeSupport.Double(payload, "page_width", 29.7));
                var pageHeight = Math.Max(0.01, BridgeSupport.Double(payload, "page_height", 21.0));
                createdLayout = LayoutFactory.Instance.CreateLayout(
                    pageWidth,
                    pageHeight,
                    ParsePageUnit(BridgeSupport.String(payload, "page_units", "CENTIMETER")));
                createdLayout.SetName(layoutName);
                if (addMapFrame)
                {
                    createdMap ??= current.GetItems<MapProjectItem>()
                        .FirstOrDefault()?.GetMap();
                    if (createdMap is null)
                    {
                        throw new InvalidOperationException(
                            "create_map_frame=true requires create_map=true or an existing map");
                    }
                    var frameName = BridgeSupport.String(payload, "map_frame_name", "Map Frame")!;
                    var bounds = BridgeSupport.Bounds(
                        payload,
                        "map_frame_bounds",
                        0.8,
                        1.8,
                        pageWidth - 0.8,
                        pageHeight - 0.8);
                    createdMapFrame = ElementFactory.Instance.CreateMapFrameElement(
                        createdLayout,
                        bounds,
                        createdMap,
                        frameName,
                        false,
                        null);
                }
            }

            var dataPaths = BridgeSupport.Strings(payload, "data_paths");
            if (dataPaths.Length > 0)
            {
                createdMap ??= current.GetItems<MapProjectItem>()
                    .FirstOrDefault()?.GetMap();
                if (createdMap is null)
                {
                    throw new InvalidOperationException("data_paths requires a map");
                }
                foreach (var dataPath in dataPaths)
                {
                    if (!File.Exists(dataPath) && !Directory.Exists(dataPath))
                    {
                        throw new FileNotFoundException($"Data path not found: {dataPath}");
                    }
                    LayerFactory.Instance.CreateLayer(new Uri(dataPath), createdMap);
                }
            }

            return (object)new
            {
                map = createdMap?.Name,
                layout = createdLayout?.Name,
                map_frame = createdMapFrame?.Name,
                data_paths_added = dataPaths.Length,
            };
        });

        // Persist the requested structure before switching to the caller's
        // exact project file. A fully saved project has stable file semantics.
        project = Project.Current ?? project;
        var structureSaved = await await FrameworkApplication.Current.Dispatcher.InvokeAsync(
            () => project.SaveAsync());
        if (!structureSaved)
        {
            throw new IOException("ArcGIS Pro could not save the bootstrap project");
        }
        var saved = await await FrameworkApplication.Current.Dispatcher.InvokeAsync(
            () => project.SaveAsAsync(outputPath, overwrite));
        if (!saved)
        {
            throw new IOException($"ArcGIS Pro could not save the new project to: {outputPath}");
        }
        project = Project.Current ?? project;
        if (!SamePath(project.Path, outputPath))
        {
            throw new IOException(
                $"ArcGIS Pro created the project at an unexpected path. Requested: {outputPath}; actual: {project.Path}");
        }

        if (openMap && createdMap is not null)
        {
            await await FrameworkApplication.Current.Dispatcher.InvokeAsync(
                () => FrameworkApplication.Panes.CreateMapPaneAsync(createdMap));
        }
        if (openLayout && createdLayout is not null)
        {
            await await FrameworkApplication.Current.Dispatcher.InvokeAsync(
                () => FrameworkApplication.Panes.CreateLayoutPaneAsync(createdLayout));
        }

        // Opening a map/layout pane can update persisted view state, so save
        // only after all requested panes have been opened.
        project = Project.Current ?? project;
        var savedProject = await await FrameworkApplication.Current.Dispatcher.InvokeAsync(
            () => project.SaveAsync());
        if (!savedProject)
        {
            throw new IOException("ArcGIS Pro could not save the newly created project");
        }
        if (!SamePath(project.Path, outputPath) || !File.Exists(outputPath))
        {
            throw new IOException(
                $"The new project did not persist at the requested path: {outputPath}");
        }

        return new
        {
            created = true,
            project = project.Name,
            project_path = project.Path,
            output_aprx = outputPath,
            path_verified = true,
            project_support_folder = supportFolder,
            home_folder = project.HomeFolderPath,
            default_geodatabase = project.DefaultGeodatabasePath,
            default_toolbox = project.DefaultToolboxPath,
            template_type = settings.TemplateType.ToString(),
            structure,
            source_project_modified = false,
            source_data_modified = false,
        };
    }

    private static LinearUnit ParsePageUnit(string? value)
    {
        return (value ?? string.Empty).ToLowerInvariant() switch
        {
            "inches" or "inch" or "in" => LinearUnit.Inches,
            "millimeters" or "millimeter" or "mm" => LinearUnit.Millimeters,
            "points" or "point" or "pt" => LinearUnit.Points,
            _ => LinearUnit.Centimeters,
        };
    }

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<object> InventoryAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        return await QueuedTask.Run<object>(() =>
        {
            var project = Project.Current
                ?? throw new InvalidOperationException("No ArcGIS Pro project is loaded.");
            var maps = project.GetItems<MapProjectItem>()
                .Select(item =>
                {
                    var map = item.GetMap();
                    return new
                    {
                        name = item.Name,
                        uri = map.URI,
                        map_type = map.MapType.ToString(),
                        spatial_reference = map.SpatialReference?.Name,
                        layer_count = map.GetLayersAsFlattenedList().Count,
                        table_count = map.GetStandaloneTablesAsFlattenedList().Count,
                        bookmark_count = map.GetBookmarks().Count,
                    };
                }).ToArray();
            var layouts = project.GetItems<LayoutProjectItem>()
                .Select(item =>
                {
                    var layout = item.GetLayout();
                    return new
                    {
                        name = item.Name,
                        uri = layout.URI,
                        element_count = layout.GetElementsAsFlattenedList().Count,
                        map_series = layout.MapSeries is null ? null : new
                        {
                            enabled = layout.MapSeries.Enabled,
                            page_count = layout.MapSeries.PageCount,
                            current_page_name = layout.MapSeries.CurrentPageName,
                            current_page_number = layout.MapSeries.CurrentPageNumber,
                        },
                    };
                }).ToArray();
            var reports = project.GetItems<ReportProjectItem>()
                .Select(item => new { name = item.Name, uri = item.GetReport().URI })
                .ToArray();
            var presentations = project.GetItems<PresentationProjectItem>()
                .Select(item =>
                {
                    var presentation = item.GetPresentation();
                    return new
                    {
                        name = item.Name,
                        uri = presentation.URI,
                        page_count = presentation.PageCount,
                    };
                }).ToArray();
            var styles = project.GetItems<StyleProjectItem>()
                .Select(item => new
                {
                    name = item.Name,
                    path = item.Path,
                    read_only = item.IsReadOnly,
                    custom = item.IsCustomStyle,
                    dictionary = item.IsDictionaryStyle,
                }).ToArray();
            return (object)new
            {
                name = project.Name,
                path = project.Path,
                uri = project.URI,
                read_only = project.ReadOnly,
                is_dirty = project.IsDirty,
                has_edits = project.HasEdits,
                is_editing_enabled = project.IsEditingEnabled,
                home_folder = project.HomeFolderPath,
                default_geodatabase = project.DefaultGeodatabasePath,
                default_toolbox = project.DefaultToolboxPath,
                maps,
                layouts,
                reports,
                presentations,
                styles,
                project_item_count = project.Items.Count,
            };
        });
    }

    internal static async Task<object> SaveAsync(JsonElement payload)
    {
        var project = Project.Current
            ?? throw new InvalidOperationException("No ArcGIS Pro project is loaded.");
        var path = project.Path;
        var success = await await FrameworkApplication.Current.Dispatcher.InvokeAsync(
            () => project.SaveAsync());
        return new
        {
            project = path,
            success,
            saved_existing_project = success,
            source_datasets_modified = false,
        };
    }
}
