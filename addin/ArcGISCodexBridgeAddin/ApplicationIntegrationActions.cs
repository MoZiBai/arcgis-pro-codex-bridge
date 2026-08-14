using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGIS.Desktop.Presentations;
using ArcGIS.Desktop.Reports;

namespace ArcGISCodexBridgeAddin;

internal static class ApplicationIntegrationActions
{
    internal static Task<object> GetPortalStateAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        return OnUiAsync<object>(() =>
        {
            var manager = ArcGISPortalManager.Current;
            var active = Safe(manager.GetActivePortal);
            var portals = manager.GetPortals().Select(portal => DescribePortal(portal, active)).ToArray();
            return new
            {
                active_portal = active is null ? null : DescribePortal(active, active),
                portals,
                portal_count = portals.Length,
                credentials_returned = false,
                tokens_returned = false,
                source_data_modified = false,
                project_modified = false,
                external_state_modified = false,
            };
        }, cancellationToken);
    }

    internal static Task<object> InspectTimeZonesAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        return OnUiAsync<object>(() =>
        {
            var manager = ProApp.IANATimeZoneManager;
            var query = BridgeSupport.String(payload, "query")?.Trim();
            var maximum = Math.Clamp(BridgeSupport.Int(payload, "max_results", 200), 1, 1000);
            var requestedId = BridgeSupport.String(payload, "time_zone_id")?.Trim();
            var zones = manager.GetTimeZones(
                    BridgeSupport.Bool(payload, "include_local", true),
                    BridgeSupport.Bool(payload, "include_unknown", false),
                    BridgeSupport.Bool(payload, "include_empty", false))
                .Where(zone => string.IsNullOrWhiteSpace(query)
                    || zone.TimeZoneId.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || zone.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || zone.LongDisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(maximum)
                .Select(zone => new
                {
                    id = zone.TimeZoneId,
                    display_name = zone.DisplayName,
                    long_display_name = zone.LongDisplayName,
                    is_iana = IANATimeZoneManager.IsIanaID(zone.TimeZoneId),
                    is_windows = IANATimeZoneManager.IsWindowsID(zone.TimeZoneId),
                    has_default_dst_offset = IANATimeZoneManager.HasDefaultDstOffset(zone.TimeZoneId),
                }).ToArray();
            object? conversion = null;
            if (!string.IsNullOrWhiteSpace(requestedId))
            {
                var isIana = IANATimeZoneManager.IsIanaID(requestedId);
                var isWindows = IANATimeZoneManager.IsWindowsID(requestedId);
                conversion = new
                {
                    input = requestedId,
                    is_iana = isIana,
                    is_windows = isWindows,
                    windows_id = isIana ? Safe(() => IANATimeZoneManager.GetWindowsIDFromIanaID(requestedId)) : null,
                    iana_id = isWindows
                        ? Safe(() => IANATimeZoneManager.GetIanaIDFromWindowsID(
                            requestedId, BridgeSupport.Bool(payload, "respect_dst", true)))
                        : null,
                };
            }
            return new
            {
                query,
                result_count = zones.Length,
                time_zones = zones,
                conversion,
                local = DescribeTimeZone(manager.GetLocalTimeZone()),
                system = DescribeTimeZone(manager.GetSystemTimeZone()),
                source_data_modified = false,
                project_modified = false,
            };
        }, cancellationToken);
    }

    internal static Task<object> InspectLocatorsAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        return OnUiAsync<object>(async () =>
        {
            var view = MapView.Active
                ?? throw new InvalidOperationException("An active map view is required to inspect locators.");
            var requestedMap = BridgeSupport.String(payload, "map_name");
            if (!string.IsNullOrWhiteSpace(requestedMap)
                && !string.Equals(view.Map.Name, requestedMap, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The requested map must be the active map view.");
            }
            var providers = await view.LocatorManager.GetLocatorInformationAsync();
            return new
            {
                map = view.Map.Name,
                locator_count = providers.Count,
                locators = providers.Select(provider => new
                {
                    name = provider.ProviderName,
                    path = provider.ProviderPath,
                    index = provider.ProviderIndex,
                    valid = provider.IsValid,
                    enabled = provider.UseProvider,
                }).ToArray(),
                source_data_modified = false,
                project_modified = false,
                external_state_modified = false,
            };
        }, cancellationToken);
    }

    internal static Task<object> GeocodeLocationAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        return OnUiAsync<object>(async () =>
        {
            var location = BridgeSupport.String(payload, "location")?.Trim();
            if (string.IsNullOrWhiteSpace(location))
                throw new ArgumentException("location is required");
            if (location.Length > 2000)
                throw new ArgumentException("location is limited to 2000 characters");
            var view = MapView.Active
                ?? throw new InvalidOperationException("An active map view is required for geocoding.");
            var requestedMap = BridgeSupport.String(payload, "map_name");
            if (!string.IsNullOrWhiteSpace(requestedMap)
                && !string.Equals(view.Map.Name, requestedMap, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The requested map must be the active map view.");
            }
            var results = await view.LocatorManager.GeocodeAsync(location, false, false, false);
            var maximum = Math.Clamp(BridgeSupport.Int(payload, "max_results", 20), 1, 200);
            return new
            {
                map = view.Map.Name,
                query = location,
                result_count = Math.Min(results.Count, maximum),
                total_returned_by_arcgis = results.Count,
                results = results.Take(maximum).Select(result => new
                {
                    label = result.Label,
                    provider = result.ProviderName,
                    score = Finite(result.Score),
                    display_location = result.DisplayLocation is null ? null : new
                    {
                        x = Finite(result.DisplayLocation.X),
                        y = Finite(result.DisplayLocation.Y),
                        z = Finite(result.DisplayLocation.Z),
                        wkid = result.DisplayLocation.SpatialReference?.Wkid,
                    },
                    extent = result.Extent is null ? null : new
                    {
                        xmin = Finite(result.Extent.XMin),
                        ymin = Finite(result.Extent.YMin),
                        xmax = Finite(result.Extent.XMax),
                        ymax = Finite(result.Extent.YMax),
                        wkid = result.Extent.SpatialReference?.Wkid,
                    },
                    fields = result.FieldValues.Take(100).ToDictionary(pair => pair.Key, pair => pair.Value),
                }).ToArray(),
                source_data_modified = false,
                project_modified = false,
                external_query_performed = true,
                external_state_modified = false,
            };
        }, cancellationToken);
    }

    internal static async Task<object> GetOperationHistoryAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        return await QueuedTask.Run<object>(() =>
        {
            var (manager, scope, name) = ResolveOperationManager(payload);
            var maximum = Math.Clamp(BridgeSupport.Int(payload, "max_operations", 100), 1, 500);
            return DescribeOperationManager(manager, scope, name, maximum);
        });
    }

    internal static async Task<object> ControlOperationHistoryAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!BridgeSupport.Bool(payload, "confirm_operation", false))
        {
            throw new ArgumentException("confirm_operation=true is required to change operation history");
        }
        var operation = (BridgeSupport.String(payload, "operation") ?? string.Empty).Trim().ToLowerInvariant();
        if (operation is not ("undo" or "redo"))
        {
            throw new ArgumentException("operation must be undo or redo");
        }
        var count = Math.Clamp(BridgeSupport.Int(payload, "count", 1), 1, 100);
        var resolved = await QueuedTask.Run(() => ResolveOperationManager(payload));
        if (operation == "undo")
        {
            if (!resolved.manager.CanUndo)
                throw new InvalidOperationException("The selected operation history has nothing to undo.");
            await resolved.manager.UndoAsync(count);
        }
        else
        {
            if (!resolved.manager.CanRedo)
                throw new InvalidOperationException("The selected operation history has nothing to redo.");
            await resolved.manager.RedoAsync(count);
        }
        var state = await QueuedTask.Run<object>(() =>
            DescribeOperationManager(resolved.manager, resolved.scope, resolved.name, 100));
        return new
        {
            operation,
            requested_count = count,
            scope = resolved.scope,
            item_name = resolved.name,
            state,
            project_modified = true,
            source_data_may_be_modified = true,
            project_saved = false,
        };
    }

    private static (OperationManager manager, string scope, string name) ResolveOperationManager(
        JsonElement payload)
    {
        if (Project.Current is null)
            throw new InvalidOperationException("No ArcGIS Pro project is loaded.");
        var scope = (BridgeSupport.String(payload, "scope") ?? "map").Trim().ToLowerInvariant();
        var name = BridgeSupport.String(payload, "item_name")?.Trim();
        return scope switch
        {
            "map" => ResolveMapManager(name),
            "layout" => ResolveLayoutManager(name),
            "report" => ResolveReportManager(name),
            "presentation" => ResolvePresentationManager(name),
            _ => throw new ArgumentException("scope must be map, layout, report, or presentation"),
        };
    }

    private static (OperationManager manager, string scope, string name) ResolveMapManager(string? name)
    {
        var map = BridgeSupport.ResolveMap(name);
        return (map.OperationManager, "map", map.Name);
    }

    private static (OperationManager manager, string scope, string name) ResolveLayoutManager(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("item_name is required for layout operation history");
        var layout = BridgeSupport.ResolveLayout(name);
        return (layout.OperationManager, "layout", layout.Name);
    }

    private static (OperationManager manager, string scope, string name) ResolveReportManager(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("item_name is required for report operation history");
        var report = Project.Current.GetItems<ReportProjectItem>()
            .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.GetReport()
            ?? throw new InvalidOperationException($"Report not found: {name}");
        return (report.OperationManager, "report", report.Name);
    }

    private static (OperationManager manager, string scope, string name) ResolvePresentationManager(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("item_name is required for presentation operation history");
        var presentation = Project.Current.GetItems<PresentationProjectItem>()
            .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.GetPresentation()
            ?? throw new InvalidOperationException($"Presentation not found: {name}");
        return (presentation.OperationManager, "presentation", presentation.Name);
    }

    private static object DescribeOperationManager(
        OperationManager manager,
        string scope,
        string name,
        int maximum)
    {
        return new
        {
            scope,
            item_name = name,
            can_undo = manager.CanUndo,
            can_redo = manager.CanRedo,
            running_operation = DescribeOperation(manager.RunningOperation),
            undo_operations = manager.FindUndoOperations(_ => true)
                .Take(maximum).Select(DescribeOperation).ToArray(),
            redo_operations = manager.FindRedoOperations(_ => true)
                .Take(maximum).Select(DescribeOperation).ToArray(),
            source_data_modified = false,
            project_modified = false,
        };
    }

    private static object? DescribeOperation(Operation? operation)
    {
        return operation is null ? null : new
        {
            id = operation.ID,
            name = operation.Name,
            category = operation.Category,
            subcategory = operation.SubCategory,
            can_undo = operation.CanUndo,
            can_redo = operation.CanRedo,
            dirties_project = operation.DirtiesProject,
        };
    }

    private static object DescribePortal(ArcGISPortal portal, ArcGISPortal? active)
    {
        return new
        {
            uri = portal.PortalUri?.ToString(),
            active = ReferenceEquals(portal, active) || Safe(portal.IsActivePortal),
            available = Safe(portal.IsPortalAvailable),
            signed_on = Safe(portal.IsSignedOn),
            username = Safe(portal.GetSignOnUsername),
        };
    }

    private static object DescribeTimeZone(IANATimeZoneData zone)
    {
        return new
        {
            id = zone.TimeZoneId,
            display_name = zone.DisplayName,
            long_display_name = zone.LongDisplayName,
        };
    }

    private static T? Safe<T>(Func<T> getter)
    {
        try { return getter(); }
        catch { return default; }
    }

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;

    private static Dispatcher UiDispatcher => Application.Current?.Dispatcher
        ?? throw new InvalidOperationException("ArcGIS Pro UI dispatcher is unavailable");

    private static async Task<T> OnUiAsync<T>(
        Func<T> callback,
        CancellationToken cancellationToken)
    {
        var dispatcher = UiDispatcher;
        if (dispatcher.CheckAccess())
            return callback();
        return await dispatcher.InvokeAsync(callback, DispatcherPriority.Normal, cancellationToken);
    }

    private static async Task<T> OnUiAsync<T>(
        Func<Task<T>> callback,
        CancellationToken cancellationToken)
    {
        var dispatcher = UiDispatcher;
        if (dispatcher.CheckAccess())
            return await callback();
        var pending = await dispatcher.InvokeAsync(callback, DispatcherPriority.Normal, cancellationToken);
        return await pending;
    }
}
