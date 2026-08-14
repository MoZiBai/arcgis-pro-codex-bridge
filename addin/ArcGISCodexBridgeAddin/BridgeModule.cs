using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal sealed class BridgeModule : ArcGIS.Desktop.Framework.Contracts.Module
{
    private static readonly string QueueRoot =
        Environment.GetEnvironmentVariable("ARCGIS_CODEX_BRIDGE_QUEUE")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArcGISCodexBridge",
            "queue");

    private CancellationTokenSource? _cancellation;
    private Task? _worker;

    protected override bool Initialize()
    {
        _cancellation = new CancellationTokenSource();
        _worker = Task.Run(() => RunQueueLoopAsync(_cancellation.Token));
        return true;
    }

    protected override bool CanUnload()
    {
        _cancellation?.Cancel();
        return true;
    }

    private static async Task RunQueueLoopAsync(CancellationToken cancellationToken)
    {
        var jobs = Directory.CreateDirectory(Path.Combine(QueueRoot, "jobs")).FullName;
        var processing = Directory.CreateDirectory(Path.Combine(QueueRoot, "processing")).FullName;
        var results = Directory.CreateDirectory(Path.Combine(QueueRoot, "results")).FullName;

        await RecoverInterruptedJobsAsync(processing, results, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                foreach (var jobPath in Directory.EnumerateFiles(jobs, "*.json").OrderBy(File.GetCreationTimeUtc))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var claimedPath = Path.Combine(processing, Path.GetFileName(jobPath));
                    try
                    {
                        File.Move(jobPath, claimedPath);
                    }
                    catch (IOException)
                    {
                        continue;
                    }

                    await ProcessJobAsync(claimedPath, results, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Keep the auto-loaded bridge alive. Per-job errors are returned
                // through the results queue; transient directory errors retry.
            }

            try
            {
                await Task.Delay(250, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task RecoverInterruptedJobsAsync(
        string processingDirectory,
        string resultsDirectory,
        CancellationToken cancellationToken)
    {
        foreach (var path in Directory.EnumerateFiles(processingDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var jobId = Path.GetFileNameWithoutExtension(path);
            try
            {
                await using var stream = File.OpenRead(path);
                using var document = await JsonDocument.ParseAsync(
                    stream, cancellationToken: cancellationToken);
                if (document.RootElement.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    jobId = id.GetString()!;
                }
            }
            catch
            {
                // The filename remains a valid fallback job identifier.
            }

            var resultPath = Path.Combine(resultsDirectory, jobId + ".json");
            var temporaryPath = resultPath + ".tmp";
            var response = JsonSerializer.Serialize(new
            {
                ok = false,
                error = "The previous ArcGIS Pro process ended while this bridge action was running. The interrupted job was recovered at startup.",
                type = "InterruptedBridgeJob",
            });
            await File.WriteAllTextAsync(temporaryPath, response, cancellationToken);
            File.Move(temporaryPath, resultPath, true);
            File.Delete(path);
        }
    }

    private static async Task ProcessJobAsync(
        string claimedPath,
        string resultsDirectory,
        CancellationToken cancellationToken)
    {
        string jobId = Path.GetFileNameWithoutExtension(claimedPath);
        object response;

        try
        {
            await using var stream = File.OpenRead(claimedPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            jobId = root.GetProperty("id").GetString() ?? jobId;
            var action = root.GetProperty("action").GetString() ?? string.Empty;
            var payload = root.TryGetProperty("payload", out var payloadElement)
                ? payloadElement.Clone()
                : default;
            var result = await DispatchAsync(action, payload, cancellationToken);
            response = new { ok = true, result };
        }
        catch (Exception exception)
        {
            response = new
            {
                ok = false,
                error = exception.Message,
                type = exception.GetType().Name,
                detail = exception.ToString(),
            };
        }

        string responseJson;
        try
        {
            responseJson = JsonSerializer.Serialize(response);
        }
        catch (Exception exception)
        {
            responseJson = JsonSerializer.Serialize(new
            {
                ok = false,
                error = $"Bridge response serialization failed: {exception.Message}",
                type = exception.GetType().Name,
            });
        }

        var resultPath = Path.Combine(resultsDirectory, jobId + ".json");
        var temporaryPath = resultPath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            responseJson,
            cancellationToken);
        File.Move(temporaryPath, resultPath, true);
        File.Delete(claimedPath);
    }

    private static Task<object> DispatchAsync(
        string action,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        return action switch
        {
            "capabilities" => Task.FromResult<object>(new
            {
                protocol_version = BridgeSupport.ProtocolVersion,
                actions = BridgeSupport.Capabilities,
                source_data_is_read_only_by_default = true,
                destructive_data_tools = false,
                source_editing_actions_present = true,
                source_editing_requires_confirmation = true,
                schema_changes_use_gated_geoprocessing = true,
                external_publishing_is_mcp_gated = true,
            }),
            "status" => GetStatusAsync(cancellationToken),
            "project_inventory" => ProjectActions.InventoryAsync(payload, cancellationToken),
            "save_project" => ProjectActions.SaveAsync(payload),
            "get_metadata" => MetadataActions.GetAsync(payload, cancellationToken),
            "set_metadata" => MetadataActions.SetAsync(payload, cancellationToken),
            "execute_batch" => ExecuteBatchAsync(payload, cancellationToken),
            "add_data" => AddDataAsync(payload, cancellationToken),
            "create_map" => MapActions.CreateAsync(payload, cancellationToken),
            "rename_map" => MapActions.RenameAsync(payload, cancellationToken),
            "duplicate_map" => MapActions.DuplicateAsync(payload, cancellationToken),
            "remove_map" => MapActions.RemoveAsync(payload, cancellationToken),
            "open_map" => MapActions.OpenAsync(payload, cancellationToken),
            "list_bookmarks" => MapActions.ListBookmarksAsync(payload, cancellationToken),
            "create_bookmark" => MapActions.CreateBookmarkAsync(payload, cancellationToken),
            "remove_bookmark" => MapActions.RemoveBookmarkAsync(payload, cancellationToken),
            "zoom_to_bookmark" => MapActions.ZoomBookmarkAsync(payload, cancellationToken),
            "configure_bookmark" => BookmarkActions.ConfigureAsync(payload, cancellationToken),
            "move_bookmark" => BookmarkActions.MoveAsync(payload, cancellationToken),
            "import_bookmarks" => BookmarkActions.ImportAsync(payload, cancellationToken),
            "export_bookmarks" => BookmarkActions.ExportAsync(payload, cancellationToken),
            "get_camera" => MapActions.GetCameraAsync(payload, cancellationToken),
            "set_camera" => MapActions.SetCameraAsync(payload, cancellationToken),
            "zoom_full_extent" => MapActions.ZoomFullExtentAsync(payload, cancellationToken),
            "get_map_view_state" => SceneViewActions.GetMapViewStateAsync(payload, cancellationToken),
            "configure_map_view" => SceneViewActions.ConfigureMapViewAsync(payload, cancellationToken),
            "get_view_filter_state" => ViewFilterActions.GetAsync(payload, cancellationToken),
            "configure_view_filters" => ViewFilterActions.ConfigureAsync(payload, cancellationToken),
            "get_active_view_context" => ViewInteractionActions.GetContextAsync(payload, cancellationToken),
            "configure_active_view_context" => ViewInteractionActions.ConfigureContextAsync(payload, cancellationToken),
            "navigate_map_view" => ViewInteractionActions.NavigateAsync(payload, cancellationToken),
            "transform_view_coordinates" => ViewInteractionActions.TransformCoordinatesAsync(payload, cancellationToken),
            "flash_features" => ViewInteractionActions.FlashFeaturesAsync(payload, cancellationToken),
            "show_feature_popup" => ViewInteractionActions.ShowPopupAsync(payload, cancellationToken),
            "close_feature_popups" => ViewInteractionActions.ClosePopupsAsync(payload, cancellationToken),
            "manage_feature_cache" => ViewInteractionActions.ManageFeatureCacheAsync(payload, cancellationToken),
            "capture_view_thumbnail" => ViewInteractionActions.CaptureThumbnailAsync(payload, cancellationToken),
            "get_map_cim" => MapActions.GetCimAsync(payload, cancellationToken),
            "set_map_cim" => MapActions.SetCimAsync(payload, cancellationToken),
            "configure_map_properties" => ProjectTemplateActions.ConfigureMapPropertiesAsync(payload, cancellationToken),
            "save_map_file" => ProjectTemplateActions.SaveMapFileAsync(payload, cancellationToken),
            "run_geoprocessing" => GeoprocessingActions.RunAsync(payload, cancellationToken),
            "describe_layer" => LayerActions.DescribeAsync(payload, cancellationToken),
            "get_layer_cim" => LayerActions.GetCimAsync(payload, cancellationToken),
            "set_layer_cim" => LayerActions.SetCimAsync(payload, cancellationToken),
            "inspect_las_layer" => SpecializedLayerActions.InspectLasAsync(payload, cancellationToken),
            "configure_las_layer" => SpecializedLayerActions.ConfigureLasAsync(payload, cancellationToken),
            "inspect_voxel_layer" => SpecializedLayerActions.InspectVoxelAsync(payload, cancellationToken),
            "configure_voxel_layer" => SpecializedLayerActions.ConfigureVoxelAsync(payload, cancellationToken),
            "get_layer_schema" => QueryActions.SchemaAsync(payload, cancellationToken),
            "query_features" => QueryActions.QueryAsync(payload, cancellationToken),
            "summarize_field" => QueryActions.SummarizeFieldAsync(payload, cancellationToken),
            "get_table_schema" => QueryActions.TableSchemaAsync(payload, cancellationToken),
            "query_table" => QueryActions.QueryTableAsync(payload, cancellationToken),
            "select_features" => QueryActions.SelectAsync(payload, cancellationToken),
            "get_selection" => QueryActions.GetSelectionAsync(payload, cancellationToken),
            "clear_selection" => QueryActions.ClearSelectionAsync(payload, cancellationToken),
            "update_feature_attributes" => EditingActions.UpdateAttributesAsync(payload, cancellationToken),
            "create_features" => EditingActions.CreateFeaturesAsync(payload, cancellationToken),
            "delete_features" => EditingActions.DeleteFeaturesAsync(payload, cancellationToken),
            "save_edits" => EditingActions.SaveEditsAsync(payload),
            "discard_edits" => EditingActions.DiscardEditsAsync(payload),
            "undo_edit" => EditingActions.UndoAsync(payload),
            "redo_edit" => EditingActions.RedoAsync(payload),
            "duplicate_layer" => LayerActions.DuplicateAsync(payload, cancellationToken),
            "create_group_layer" => LayerActions.CreateGroupAsync(payload, cancellationToken),
            "add_standalone_table" => LayerActions.AddStandaloneTableAsync(payload, cancellationToken),
            "manage_standalone_table" => LayerActions.ManageStandaloneTableAsync(payload, cancellationToken),
            "remove_standalone_table" => LayerActions.RemoveStandaloneTableAsync(payload, cancellationToken),
            "get_standalone_table_cim" => CimObjectActions.GetStandaloneTableAsync(payload, cancellationToken),
            "set_standalone_table_cim" => CimObjectActions.SetStandaloneTableAsync(payload, cancellationToken),
            "manage_layers" => LayerActions.ManageAsync(payload, cancellationToken),
            "remove_layers" => LayerActions.RemoveAsync(payload, cancellationToken),
            "reorder_layer" => LayerActions.ReorderAsync(payload, cancellationToken),
            "zoom_to_layer" => LayerActions.ZoomAsync(payload, cancellationToken),
            "apply_symbology" => ApplySymbologyAsync(payload, cancellationToken),
            "apply_vector_symbology" => SymbologyActions.ApplyVectorAsync(payload, cancellationToken),
            "configure_visual_variables" => SymbologyActions.ConfigureVisualVariablesAsync(payload, cancellationToken),
            "apply_raster_symbology" => SymbologyActions.ApplyRasterAsync(payload, cancellationToken),
            "configure_raster_rendering_rule" => SymbologyActions.ConfigureRasterRenderingRuleAsync(payload, cancellationToken),
            "configure_multidimensional_raster" => SymbologyActions.ConfigureMultidimensionalRasterAsync(payload, cancellationToken),
            "list_elevation_surfaces" => SceneViewActions.ListElevationSurfacesAsync(payload, cancellationToken),
            "configure_elevation_surface" => SceneViewActions.ConfigureElevationSurfaceAsync(payload, cancellationToken),
            "configure_layer_elevation" => SceneViewActions.ConfigureLayerElevationAsync(payload, cancellationToken),
            "sample_elevation_surface" => SceneViewActions.SampleElevationAsync(payload, cancellationToken),
            "get_elevation_profile" => SceneViewActions.GetElevationProfileAsync(payload, cancellationToken),
            "get_animation_state" => SceneViewActions.GetAnimationStateAsync(payload, cancellationToken),
            "control_animation" => SceneViewActions.ControlAnimationAsync(payload, cancellationToken),
            "begin_animation_export" => SceneViewActions.BeginAnimationExportAsync(payload, cancellationToken),
            "configure_labels" => LayerActions.ConfigureLabelsAsync(payload, cancellationToken),
            "list_label_classes" => LayerActions.ListLabelClassesAsync(payload, cancellationToken),
            "configure_label_class" => LayerActions.ConfigureLabelClassAsync(payload, cancellationToken),
            "remove_label_classes" => LayerActions.RemoveLabelClassesAsync(payload, cancellationToken),
            "configure_popup" => LayerActions.ConfigurePopupAsync(payload, cancellationToken),
            "configure_fields" => LayerActions.ConfigureFieldsAsync(payload, cancellationToken),
            "configure_display_filters" => LayerActions.ConfigureDisplayFiltersAsync(payload, cancellationToken),
            "configure_time" => LayerActions.ConfigureTimeAsync(payload, cancellationToken),
            "get_charts" => LayerActions.GetChartsAsync(payload, cancellationToken),
            "set_charts" => LayerActions.SetChartsAsync(payload, cancellationToken),
            "draw_graphics" => GraphicsActions.DrawAsync(payload, cancellationToken),
            "list_map_graphics" => GraphicsActions.ListAsync(payload, cancellationToken),
            "manage_map_graphics" => GraphicsActions.ManageAsync(payload, cancellationToken),
            "get_map_graphic_cim" => GraphicsActions.GetCimAsync(payload, cancellationToken),
            "set_map_graphic_cim" => GraphicsActions.SetCimAsync(payload, cancellationToken),
            "create_layout" => LayoutActions.CreateAsync(payload, cancellationToken),
            "duplicate_layout" => LayoutActions.DuplicateLayoutAsync(payload, cancellationToken),
            "rename_layout" => LayoutActions.RenameLayoutAsync(payload, cancellationToken),
            "remove_layout" => LayoutActions.RemoveLayoutAsync(payload, cancellationToken),
            "open_layout" => LayoutActions.OpenLayoutAsync(payload, cancellationToken),
            "configure_layout_page" => ProjectTemplateActions.ConfigureLayoutPageAsync(payload, cancellationToken),
            "manage_layout_guides" => ProjectTemplateActions.ManageLayoutGuidesAsync(payload, cancellationToken),
            "save_layout_file" => ProjectTemplateActions.SaveLayoutFileAsync(payload, cancellationToken),
            "add_map_frame" => LayoutActions.AddMapFrameAsync(payload, cancellationToken),
            "list_dynamic_frames" => DynamicFrameActions.ListAsync(payload, cancellationToken),
            "add_table_frame" => DynamicFrameActions.AddTableFrameAsync(payload, cancellationToken),
            "add_chart_frame" => DynamicFrameActions.AddChartFrameAsync(payload, cancellationToken),
            "configure_map_frame" => LayoutActions.ConfigureMapFrameAsync(payload, cancellationToken),
            "list_extent_indicators" => AdvancedCartographyActions.ListExtentIndicatorsAsync(payload, cancellationToken),
            "add_extent_indicator" => AdvancedCartographyActions.AddExtentIndicatorAsync(payload, cancellationToken),
            "configure_extent_indicator" => AdvancedCartographyActions.ConfigureExtentIndicatorAsync(payload, cancellationToken),
            "remove_extent_indicators" => AdvancedCartographyActions.RemoveExtentIndicatorsAsync(payload, cancellationToken),
            "configure_map_clipping" => AdvancedCartographyActions.ConfigureMapClippingAsync(payload, cancellationToken),
            "add_dynamic_text" => AdvancedCartographyActions.AddDynamicTextAsync(payload, cancellationToken),
            "create_map_series" => LayoutActions.CreateMapSeriesAsync(payload, cancellationToken),
            "list_map_series" => MapSeriesActions.ListAsync(payload, cancellationToken),
            "configure_map_series" => MapSeriesActions.ConfigureAsync(payload, cancellationToken),
            "remove_map_series" => MapSeriesActions.RemoveAsync(payload, cancellationToken),
            "export_map_series" => MapSeriesActions.ExportAsync(payload, cancellationToken),
            "arrange_layout_elements" => AdvancedLayoutActions.ArrangeElementsAsync(payload, cancellationToken),
            "export_map_view" => MapViewExportActions.ExportAsync(payload, cancellationToken),
            "get_layout_cim" => LayoutActions.GetCimAsync(payload, cancellationToken),
            "set_layout_cim" => LayoutActions.SetCimAsync(payload, cancellationToken),
            "get_layout_element_cim" => CimObjectActions.GetLayoutElementAsync(payload, cancellationToken),
            "set_layout_element_cim" => CimObjectActions.SetLayoutElementAsync(payload, cancellationToken),
            "create_layout_element_from_cim" => CimObjectActions.CreateLayoutElementAsync(payload, cancellationToken),
            "update_layout_element" => LayoutActions.UpdateElementAsync(payload, cancellationToken),
            "organize_layout_elements" => LayoutActions.OrganizeElementsAsync(payload, cancellationToken),
            "add_layout_picture" => LayoutActions.AddPictureAsync(payload, cancellationToken),
            "add_layout_graphics" => LayoutActions.AddGraphicsAsync(payload, cancellationToken),
            "add_layout_ticks" => LayoutActions.AddTicksAsync(payload, cancellationToken),
            "add_layout_colorbar" => LayoutActions.AddColorbarAsync(payload, cancellationToken),
            "add_raster_colorbar_from_layer" => LayoutActions.AddRasterColorbarFromLayerAsync(payload, cancellationToken),
            "import_layout" => LayoutActions.ImportAsync(payload, cancellationToken),
            "set_layout_text" => LayoutActions.SetTextAsync(payload, cancellationToken),
            "zoom_layout" => LayoutActions.ZoomFrameAsync(payload, cancellationToken),
            "export_layout" => ExportLayoutAsync(payload, cancellationToken),
            "save_copy" or "save_project_as" => LayoutActions.SaveProjectAsAsync(payload),
            "list_reports" => ReportActions.ListAsync(payload, cancellationToken),
            "create_report" => ReportActions.CreateAsync(payload, cancellationToken),
            "configure_report" => ReportActions.ConfigureAsync(payload, cancellationToken),
            "get_report_cim" => CimObjectActions.GetReportAsync(payload, cancellationToken),
            "set_report_cim" => CimObjectActions.SetReportAsync(payload, cancellationToken),
            "get_report_element_cim" => CimObjectActions.GetReportElementAsync(payload, cancellationToken),
            "set_report_element_cim" => CimObjectActions.SetReportElementAsync(payload, cancellationToken),
            "duplicate_report" => ReportActions.DuplicateAsync(payload, cancellationToken),
            "rename_report" => ReportActions.RenameAsync(payload, cancellationToken),
            "remove_report" => ReportActions.RemoveAsync(payload, cancellationToken),
            "export_report_pdf" => ReportActions.ExportPdfAsync(payload, cancellationToken),
            "list_catalog_items" => CatalogActions.ListAsync(payload, cancellationToken),
            "add_project_item" => CatalogActions.AddProjectItemAsync(payload, cancellationToken),
            "remove_project_item" => CatalogActions.RemoveProjectItemAsync(payload, cancellationToken),
            "repair_project_item" => CatalogActions.RepairProjectItemAsync(payload, cancellationToken),
            "manage_favorite" => CatalogActions.ManageFavoriteAsync(payload, cancellationToken),
            "list_presentations" => PresentationActions.ListAsync(payload, cancellationToken),
            "create_presentation" => PresentationActions.CreateAsync(payload, cancellationToken),
            "get_presentation_cim" => CimObjectActions.GetPresentationAsync(payload, cancellationToken),
            "set_presentation_cim" => CimObjectActions.SetPresentationAsync(payload, cancellationToken),
            "get_presentation_page_cim" => CimObjectActions.GetPresentationPageAsync(payload, cancellationToken),
            "set_presentation_page_cim" => CimObjectActions.SetPresentationPageAsync(payload, cancellationToken),
            "get_presentation_page_element_cim" => CimObjectActions.GetPresentationPageElementAsync(payload, cancellationToken),
            "set_presentation_page_element_cim" => CimObjectActions.SetPresentationPageElementAsync(payload, cancellationToken),
            "duplicate_presentation" => PresentationActions.DuplicateAsync(payload, cancellationToken),
            "rename_presentation" => PresentationActions.RenameAsync(payload, cancellationToken),
            "remove_presentation" => PresentationActions.RemoveAsync(payload, cancellationToken),
            "open_presentation" => PresentationActions.OpenAsync(payload, cancellationToken),
            "configure_presentation" => PresentationActions.ConfigureAsync(payload, cancellationToken),
            "add_presentation_page" => PresentationActions.AddPageAsync(payload, cancellationToken),
            "configure_presentation_page" => PresentationActions.ConfigurePageAsync(payload, cancellationToken),
            "remove_presentation_pages" => PresentationActions.RemovePagesAsync(payload, cancellationToken),
            "export_presentation" => PresentationActions.ExportAsync(payload, cancellationToken),
            "save_presentation_file" => PresentationActions.SaveFileAsync(payload, cancellationToken),
            "list_attachments" => AttachmentRelationshipActions.ListAttachmentsAsync(payload, cancellationToken),
            "export_attachment" => AttachmentRelationshipActions.ExportAttachmentAsync(payload, cancellationToken),
            "add_attachment" => AttachmentRelationshipActions.AddAttachmentAsync(payload, cancellationToken),
            "update_attachment" => AttachmentRelationshipActions.UpdateAttachmentAsync(payload, cancellationToken),
            "remove_attachments" => AttachmentRelationshipActions.RemoveAttachmentsAsync(payload, cancellationToken),
            "list_relationships" => AttachmentRelationshipActions.ListRelationshipsAsync(payload, cancellationToken),
            "query_related_records" => AttachmentRelationshipActions.QueryRelatedRecordsAsync(payload, cancellationToken),
            "list_style_items" => CartographyActions.ListStyleItemsAsync(payload, cancellationToken),
            "list_map_grids" => CartographyActions.ListMapGridsAsync(payload, cancellationToken),
            "add_map_grid" => CartographyActions.AddMapGridAsync(payload, cancellationToken),
            "set_map_grid_cim" => CartographyActions.SetMapGridCimAsync(payload, cancellationToken),
            "configure_map_grid" => CartographyActions.ConfigureMapGridAsync(payload, cancellationToken),
            "remove_map_grids" => CartographyActions.RemoveMapGridsAsync(payload, cancellationToken),
            "list_map_surrounds" => CartographyActions.ListMapSurroundsAsync(payload, cancellationToken),
            "add_map_surround" => CartographyActions.AddMapSurroundAsync(payload, cancellationToken),
            "configure_map_surround" => CartographyActions.ConfigureMapSurroundAsync(payload, cancellationToken),
            "set_map_surround_cim" => CartographyActions.SetMapSurroundCimAsync(payload, cancellationToken),
            "get_data_connection" => DataSourceActions.GetConnectionAsync(payload, cancellationToken),
            "set_data_connection" => DataSourceActions.SetConnectionAsync(payload, cancellationToken),
            "replace_data_source" => DataSourceActions.ReplaceDataSourceAsync(payload, cancellationToken),
            "spatial_query_features" => SpatialQueryActions.QueryAsync(payload, cancellationToken),
            "spatial_select_features" => SpatialQueryActions.SelectAsync(payload, cancellationToken),
            "list_task_items" => TaskWorkflowActions.ListTasksAsync(payload, cancellationToken),
            "manage_task" => TaskWorkflowActions.ManageTaskAsync(payload, cancellationToken),
            "get_workflow_status" => TaskWorkflowActions.GetWorkflowStatusAsync(payload, cancellationToken),
            "search_workflow_jobs" => TaskWorkflowActions.SearchWorkflowJobsAsync(payload, cancellationToken),
            "get_workflow_job" => TaskWorkflowActions.GetWorkflowJobAsync(payload, cancellationToken),
            "control_workflow_job" => TaskWorkflowActions.ControlWorkflowJobAsync(payload, cancellationToken),
            "list_knowledge_graphs" => KnowledgeGraphActions.ListAsync(payload, cancellationToken),
            "manage_knowledge_graph_investigation" => KnowledgeGraphActions.ManageInvestigationAsync(payload, cancellationToken),
            "query_knowledge_graph" => KnowledgeGraphActions.QueryAsync(payload, cancellationToken),
            "get_link_chart_state" => KnowledgeGraphActions.GetLinkChartStateAsync(payload, cancellationToken),
            "configure_link_chart" => KnowledgeGraphActions.ConfigureLinkChartAsync(payload, cancellationToken),
            "inspect_network_topology_layer" => NetworkTopologyActions.InspectAsync(payload, cancellationToken),
            "get_framework_state" => FrameworkActions.GetStateAsync(payload, cancellationToken),
            "inspect_framework_command" => FrameworkActions.InspectCommandAsync(payload, cancellationToken),
            "execute_framework_command" => FrameworkActions.ExecuteCommandAsync(payload, cancellationToken),
            "configure_framework_application" => FrameworkActions.ConfigureApplicationAsync(payload, cancellationToken),
            "manage_framework_pane" => FrameworkActions.ManagePaneAsync(payload, cancellationToken),
            "get_portal_state" => ApplicationIntegrationActions.GetPortalStateAsync(payload, cancellationToken),
            "inspect_time_zones" => ApplicationIntegrationActions.InspectTimeZonesAsync(payload, cancellationToken),
            "inspect_locators" => ApplicationIntegrationActions.InspectLocatorsAsync(payload, cancellationToken),
            "geocode_location" => ApplicationIntegrationActions.GeocodeLocationAsync(payload, cancellationToken),
            "get_operation_history" => ApplicationIntegrationActions.GetOperationHistoryAsync(payload, cancellationToken),
            "control_operation_history" => ApplicationIntegrationActions.ControlOperationHistoryAsync(payload, cancellationToken),
            "geometry_engine" => GeometryEngineActions.ExecuteAsync(payload, cancellationToken),
            "manage_exploratory_analysis" => ExploratoryAnalysisActions.ManageAsync(payload, cancellationToken),
            "manage_report_elements" => DocumentElementFactoryActions.ManageReportAsync(payload, cancellationToken),
            "manage_presentation_elements" => DocumentElementFactoryActions.ManagePresentationAsync(payload, cancellationToken),
            "configure_specialized_renderer" => SpecializedRenderingActions.ConfigureAsync(payload, cancellationToken),
            "inspect_printers" => SpecializedRenderingActions.InspectPrintersAsync(payload, cancellationToken),
            "manage_report_composition" => AdvancedDocumentActions.ManageReportCompositionAsync(payload, cancellationToken),
            "configure_presentation_transition" => AdvancedDocumentActions.ConfigurePresentationTransitionAsync(payload, cancellationToken),
            "evaluate_attribute_rules" => AttributeRuleActions.EvaluateAsync(payload, cancellationToken),
            _ => throw new NotSupportedException(
                $"The installed add-in does not yet support live action '{action}'.")
        };
    }

    private static async Task<object> ExecuteBatchAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!payload.TryGetProperty("commands", out var commands)
            || commands.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("commands array is required");
        }

        var results = new List<object>();
        var index = 0;
        foreach (var command in commands.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            var action = command.GetProperty("action").GetString()
                ?? throw new ArgumentException($"Command {index} has no action");
            if (action == "execute_batch")
            {
                throw new ArgumentException("Nested execute_batch commands are not supported");
            }
            var commandPayload = command.TryGetProperty("payload", out var itemPayload)
                ? itemPayload.Clone()
                : default;
            try
            {
                var result = await DispatchAsync(action, commandPayload, cancellationToken);
                results.Add(new { index, action, ok = true, result });
            }
            catch (Exception exception)
            {
                results.Add(new
                {
                    index,
                    action,
                    ok = false,
                    error = exception.Message,
                    type = exception.GetType().Name,
                });
                if (!BridgeSupport.Bool(payload, "continue_on_error", false))
                {
                    break;
                }
            }
        }
        return new { results };
    }

    private static async Task<object> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (Project.Current is null)
        {
            return new
            {
                available = false,
                message = "ArcGIS Pro is open, but no project is loaded.",
                addin_version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            };
        }

        return await QueuedTask.Run(() =>
        {
            var maps = Project.Current.GetItems<MapProjectItem>()
                .Select(item => item.Name)
                .ToArray();
            var activeLayers = MapView.Active?.Map?.GetLayersAsFlattenedList()
                .Select(layer => new
                {
                    name = layer.Name,
                    type = layer.GetType().Name,
                    visible = layer.IsVisible,
                    transparency = layer.Transparency,
                    uri = layer.URI,
                })
                .ToArray() ?? Array.Empty<object>();
            var layouts = Project.Current.GetItems<LayoutProjectItem>()
                .Select(item =>
                {
                    var layout = item.GetLayout();
                    return new
                    {
                        name = layout.Name,
                        elements = layout.GetElementsAsFlattenedList()
                            .Select(element => new
                            {
                                name = element.Name,
                                type = element.GetType().Name,
                                visible = element.IsVisible,
                            }).ToArray(),
                    };
                }).ToArray();
            return (object)new
            {
                available = true,
                project = Project.Current.Path,
                active_map = MapView.Active?.Map?.Name,
                maps,
                active_layers = activeLayers,
                layouts,
                addin_version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
                protocol_version = BridgeSupport.ProtocolVersion,
                capabilities = BridgeSupport.Capabilities,
                queue_root = QueueRoot,
            };
        });
    }

    private static async Task<object> AddDataAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (Project.Current is null)
        {
            throw new InvalidOperationException("No ArcGIS Pro project is loaded.");
        }

        var paths = payload.GetProperty("data_paths")
            .EnumerateArray()
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
        var requestedMap = payload.TryGetProperty("map_name", out var mapNameElement)
            && mapNameElement.ValueKind == JsonValueKind.String
            ? mapNameElement.GetString()
            : null;

        return await QueuedTask.Run(() =>
        {
            Map? map = null;
            if (!string.IsNullOrWhiteSpace(requestedMap))
            {
                map = Project.Current.GetItems<MapProjectItem>()
                    .FirstOrDefault(item => string.Equals(item.Name, requestedMap, StringComparison.OrdinalIgnoreCase))
                    ?.GetMap();
            }
            map ??= MapView.Active?.Map;
            map ??= Project.Current.GetItems<MapProjectItem>().FirstOrDefault()?.GetMap();
            if (map is null)
            {
                throw new InvalidOperationException("The current project has no map.");
            }

            var added = new List<string>();
            foreach (var path in paths)
            {
                if (!DatasetPathExists(path))
                {
                    throw new FileNotFoundException("Dataset path was not found.", path);
                }
                var layer = LayerFactory.Instance.CreateLayer(new Uri(path), map);
                added.Add(layer.Name);
            }

            return (object)new
            {
                map = map.Name,
                added,
            };
        });
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

    private static async Task<object> ApplySymbologyAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = payload.GetProperty("layer_name").GetString()
            ?? throw new ArgumentException("layer_name is required");
        var preset = payload.GetProperty("preset").GetString()
            ?? throw new ArgumentException("preset is required");
        var requestedMap = payload.TryGetProperty("map_name", out var mapNameElement)
            && mapNameElement.ValueKind == JsonValueKind.String
            ? mapNameElement.GetString()
            : null;

        return await QueuedTask.Run(() =>
        {
            var map = ResolveMap(requestedMap);
            var layer = map.GetLayersAsFlattenedList()
                .OfType<FeatureLayer>()
                .FirstOrDefault(item => string.Equals(item.Name, layerName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Feature layer not found: {layerName}");

            CIMSymbol symbol = preset.ToLowerInvariant() switch
            {
                "roads" => SymbolFactory.Instance.ConstructLineSymbol(
                    ColorFactory.Instance.CreateRGBColor(210, 55, 45), 2.2, SimpleLineStyle.Solid),
                "rivers" => SymbolFactory.Instance.ConstructLineSymbol(
                    ColorFactory.Instance.CreateRGBColor(40, 120, 220), 2.0, SimpleLineStyle.Solid),
                "points" => SymbolFactory.Instance.ConstructPointSymbol(
                    ColorFactory.Instance.CreateRGBColor(235, 135, 35), 6.0, SimpleMarkerStyle.Circle),
                "buffer" => SymbolFactory.Instance.ConstructPolygonSymbol(
                    ColorFactory.Instance.CreateRGBColor(255, 165, 40, 45),
                    SimpleFillStyle.Solid,
                    SymbolFactory.Instance.ConstructStroke(
                        ColorFactory.Instance.CreateRGBColor(235, 120, 20), 1.5, SimpleLineStyle.Solid)),
                "impact" => SymbolFactory.Instance.ConstructPolygonSymbol(
                    ColorFactory.Instance.CreateRGBColor(155, 95, 190, 50),
                    SimpleFillStyle.Solid,
                    SymbolFactory.Instance.ConstructStroke(
                        ColorFactory.Instance.CreateRGBColor(95, 45, 130), 1.2, SimpleLineStyle.Solid)),
                "boundary" => SymbolFactory.Instance.ConstructPolygonSymbol(
                    ColorFactory.Instance.CreateRGBColor(255, 255, 255, 0),
                    SimpleFillStyle.Solid,
                    SymbolFactory.Instance.ConstructStroke(
                        ColorFactory.Instance.CreateRGBColor(35, 35, 35), 2.0, SimpleLineStyle.Solid)),
                _ => SymbolFactory.Instance.ConstructPolygonSymbol(
                    ColorFactory.Instance.CreateRGBColor(245, 215, 115, 45),
                    SimpleFillStyle.Solid,
                    SymbolFactory.Instance.ConstructStroke(
                        ColorFactory.Instance.CreateRGBColor(120, 95, 45), 0.8, SimpleLineStyle.Solid)),
            };

            layer.SetRenderer(new CIMSimpleRenderer { Symbol = symbol.MakeSymbolReference() });
            return (object)new { layer = layer.Name, preset };
        });
    }

    private static async Task<object> CreateLayoutAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = payload.GetProperty("layout_name").GetString()
            ?? throw new ArgumentException("layout_name is required");
        var title = payload.TryGetProperty("title", out var titleElement)
            && titleElement.ValueKind == JsonValueKind.String
            ? titleElement.GetString()
            : null;
        var requestedMap = payload.TryGetProperty("map_name", out var mapNameElement)
            && mapNameElement.ValueKind == JsonValueKind.String
            ? mapNameElement.GetString()
            : null;
        var extentLayerName = payload.TryGetProperty("extent_layer_name", out var extentLayerElement)
            && extentLayerElement.ValueKind == JsonValueKind.String
            ? extentLayerElement.GetString()
            : null;
        var legendLayerNames = payload.TryGetProperty("legend_layer_names", out var legendLayersElement)
            && legendLayersElement.ValueKind == JsonValueKind.Array
            ? legendLayersElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var openLayout = !payload.TryGetProperty("open_layout", out var openLayoutElement)
            || openLayoutElement.ValueKind != JsonValueKind.False;

        Layout? createdLayout = null;

        var result = await QueuedTask.Run(() =>
        {
            if (Project.Current.GetItems<LayoutProjectItem>()
                .Any(item => string.Equals(item.Name, layoutName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Layout already exists: {layoutName}");
            }

            var map = ResolveMap(requestedMap);
            var layout = LayoutFactory.Instance.CreateLayout(29.7, 21.0, LinearUnit.Centimeters);
            layout.SetName(layoutName);
            createdLayout = layout;

            // Keep the arrangement deliberately simple and flexible: a dominant
            // central map, centered scale bar, north arrow at upper right, and a
            // legend sized by ArcGIS Pro at lower right. Every item remains an
            // ordinary editable layout element in the visible project.
            var hasTitle = !string.IsNullOrWhiteSpace(title);
            var frameEnvelope = EnvelopeBuilderEx.CreateEnvelope(
                0.8,
                1.9,
                28.9,
                hasTitle ? 19.3 : 20.2);
            var mapFrame = ElementFactory.Instance.CreateMapFrameElement(
                layout, frameEnvelope, map, "主图", false, null);

            var extentLayer = !string.IsNullOrWhiteSpace(extentLayerName)
                ? map.GetLayersAsFlattenedList()
                    .FirstOrDefault(layer => string.Equals(
                        layer.Name, extentLayerName, StringComparison.OrdinalIgnoreCase))
                : null;
            if (extentLayer is not null)
            {
                mapFrame.SetCamera(extentLayer, false);
                var camera = mapFrame.Camera;
                camera.Scale *= 1.08;
                mapFrame.SetCamera(camera);
            }
            else if (MapView.Active?.Map == map && MapView.Active.Camera is not null)
            {
                mapFrame.SetCamera(MapView.Active.Camera);
            }

            var borderSymbol = SymbolFactory.Instance.ConstructPolygonSymbol(
                ColorFactory.Instance.CreateRGBColor(255, 255, 255, 0),
                SimpleFillStyle.Solid,
                SymbolFactory.Instance.ConstructStroke(
                    ColorFactory.Instance.CreateRGBColor(45, 45, 45),
                    0.8,
                    SimpleLineStyle.Solid));
            ElementFactory.Instance.CreatePredefinedShapeGraphicElement(
                layout,
                PredefinedShape.Rectangle,
                frameEnvelope,
                borderSymbol,
                "主图边框",
                false,
                null);

            if (hasTitle)
            {
                var titleSymbol = SymbolFactory.Instance.ConstructTextSymbol(
                    ColorFactory.Instance.CreateRGBColor(30, 30, 30),
                    18,
                    "Microsoft YaHei",
                    "Bold");
                titleSymbol.HorizontalAlignment = HorizontalAlignment.Center;
                titleSymbol.VerticalAlignment = VerticalAlignment.Center;
                ElementFactory.Instance.CreateTextGraphicElement(
                    layout,
                    TextType.PointText,
                    MapPointBuilderEx.CreateMapPoint(14.85, 20.05),
                    titleSymbol,
                    title,
                    "标题",
                    false,
                    null);
            }

            var legend = ElementFactory.Instance.CreateMapSurroundElement(
                layout,
                EnvelopeBuilderEx.CreateEnvelope(23.0, 2.5, 28.35, 8.6),
                new LegendInfo { MapFrameName = mapFrame.Name },
                "图例",
                false,
                null) as Legend;
            if (legend is not null)
            {
                var definition = (CIMLegend)legend.GetDefinition();
                definition.Title = "图例";
                definition.ShowTitle = true;
                definition.AutoFonts = true;
                definition.AutoVisibility = false;
                definition.FittingStrategy = LegendFittingStrategy.AdjustFrame;
                if (legendLayerNames is not null && legendLayerNames.Count > 0)
                {
                    definition.ExcludedLayers = map.GetLayersAsFlattenedList()
                        .Where(layer => !legendLayerNames.Contains(layer.Name))
                        .Select(layer => layer.URI)
                        .ToArray();
                }
                legend.SetDefinition(definition);
            }

            var northArrow = ElementFactory.Instance.CreateMapSurroundElement(
                layout,
                EnvelopeBuilderEx.CreateEnvelope(26.0, 16.8, 28.15, 19.0),
                new NorthArrowInfo { MapFrameName = mapFrame.Name },
                "指北针",
                false,
                null);

            var scaleBar = ElementFactory.Instance.CreateMapSurroundElement(
                layout,
                EnvelopeBuilderEx.CreateEnvelope(11.5, 0.55, 18.2, 1.55),
                new ScaleBarInfo { MapFrameName = mapFrame.Name },
                "比例尺",
                false,
                null) as ScaleBar;
            if (scaleBar is not null)
            {
                var definition = (CIMScaleBar)scaleBar.GetDefinition();
                definition.Units = LinearUnit.Kilometers;
                definition.UnitLabel = "km";
                definition.Divisions = 4;
                definition.Subdivisions = 2;
                definition.FittingStrategy = ScaleBarFittingStrategy.AdjustDivision;
                scaleBar.SetDefinition(definition);
            }

            return (object)new
            {
                layout = layout.Name,
                map = map.Name,
                map_frame = mapFrame.Name,
                elements = new[]
                {
                    mapFrame.Name,
                    legend?.Name,
                    northArrow.Name,
                    scaleBar?.Name,
                    hasTitle ? "标题" : null,
                }.Where(name => name is not null).ToArray(),
                page = "A4 landscape",
            };
        });

        if (openLayout && createdLayout is not null)
        {
            await await FrameworkApplication.Current.Dispatcher.InvokeAsync(
                () => FrameworkApplication.Panes.CreateLayoutPaneAsync(createdLayout));
        }

        return result;
    }

    private static async Task<object> ExportLayoutAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = payload.GetProperty("layout_name").GetString()
            ?? throw new ArgumentException("layout_name is required");
        var output = payload.GetProperty("output_file").GetString()
            ?? throw new ArgumentException("output_file is required");
        var dpi = payload.TryGetProperty("dpi", out var dpiElement) ? dpiElement.GetInt32() : 300;
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? QueueRoot);

        return await QueuedTask.Run(() =>
        {
            var item = Project.Current.GetItems<LayoutProjectItem>()
                .FirstOrDefault(layout => string.Equals(layout.Name, layoutName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Layout not found: {layoutName}");
            var layout = item.GetLayout();
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                dpi,
                embed_fonts = BridgeSupport.Bool(payload, "embed_fonts", true),
                full_rasterization = BridgeSupport.Bool(payload, "full_rasterization", false),
                convert_character_markers = BridgeSupport.Bool(payload, "convert_character_markers", false),
                compress_vector_graphics = BridgeSupport.Bool(payload, "compress_vector_graphics", true),
                include_nonvisible_map_layers = BridgeSupport.Bool(payload, "include_nonvisible_map_layers", false),
            }));
            layout.Export(ExportSupport.CreateFormat(output, document.RootElement));
            return (object)new { layout = layout.Name, output, dpi };
        });
    }

    private static Map ResolveMap(string? requestedMap)
    {
        Map? map = null;
        if (!string.IsNullOrWhiteSpace(requestedMap))
        {
            map = Project.Current.GetItems<MapProjectItem>()
                .FirstOrDefault(item => string.Equals(item.Name, requestedMap, StringComparison.OrdinalIgnoreCase))
                ?.GetMap();
        }
        map ??= MapView.Active?.Map;
        map ??= Project.Current.GetItems<MapProjectItem>().FirstOrDefault()?.GetMap();
        return map ?? throw new InvalidOperationException("The current project has no map.");
    }
}
