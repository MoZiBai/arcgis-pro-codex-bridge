using System.Text.Json;
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
