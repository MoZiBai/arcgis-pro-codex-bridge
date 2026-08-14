using System.Collections;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

namespace ArcGISCodexBridgeAddin;

internal static class FrameworkActions
{
    internal static Task<object> GetStateAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        return OnUiAsync(() => DescribeState(payload), cancellationToken);
    }

    private static object DescribeState(JsonElement payload)
    {
        var panes = FrameworkApplication.Panes.Cast<Pane>().Select(DescribePane).ToArray();
        var dockPanes = FrameworkApplication.DockPaneManager.DockPanes.Select(DescribeDockPane).ToArray();
        var addIns = BridgeSupport.Bool(payload, "include_addins", false)
            ? FrameworkApplication.GetAddInInfos().Select(info => new
            {
                id = info.ID,
                name = info.Name,
                version = info.Version,
                target_version = info.TargetVersion,
                author = info.Author,
                company = info.Company,
                compatible = info.IsCompatible,
                deleted = info.IsDeleted,
                error = info.ErrorMsg,
                path = info.FullPath,
                signature = info.DigitalSignature,
            }).ToArray()
            : Array.Empty<object>();
        return new
        {
            initialized = FrameworkApplication.Initialized,
            active = FrameworkApplication.IsActive,
            busy = FrameworkApplication.IsBusy,
            enabled = FrameworkApplication.IsEnabled,
            backstage_open = FrameworkApplication.IsBackstageOpen,
            active_tab = FrameworkApplication.ActiveTab,
            current_tool = FrameworkApplication.CurrentTool,
            incoming_tool = FrameworkApplication.IncomingTool,
            outgoing_tool = FrameworkApplication.OutgoingTool,
            ribbon_minimized = FrameworkApplication.IsRibbonMinimized,
            scale_factor = FrameworkApplication.ScaleFactor,
            theme = FrameworkApplication.ApplicationTheme.ToString(),
            active_pane = FrameworkApplication.Panes.ActivePane is null
                ? null
                : DescribePane(FrameworkApplication.Panes.ActivePane),
            panes,
            dock_panes = dockPanes,
            addins = addIns,
            source_data_modified = false,
        };
    }

    internal static Task<object> InspectCommandAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        return OnUiAsync<object>(() =>
        {
            var commandId = Required(payload, "command_id");
            var wrapper = FrameworkApplication.GetPlugInWrapper(
                commandId, BridgeSupport.Bool(payload, "load_on_demand", false));
            return wrapper is null
                ? new
                {
                    command_id = commandId,
                    found = false,
                    source_data_modified = false,
                }
                : new
                {
                    command_id = commandId,
                    found = true,
                    command_type = wrapper.CommandType.ToString(),
                    caption = wrapper.Caption,
                    enabled = wrapper.Enabled,
                    relevant = wrapper.IsRelevant,
                    @checked = wrapper.Checked,
                    tooltip_heading = wrapper.TooltipHeading,
                    tooltip = wrapper.Tooltip,
                    disabled_tooltip = wrapper.DisabledTooltip,
                    keytip = wrapper.Keytip,
                    children = wrapper.Children ?? Array.Empty<string>(),
                    source_data_modified = false,
                };
        }, cancellationToken);
    }

    internal static async Task<object> ExecuteCommandAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        return await OnUiAsync<object>(async () =>
        {
            if (!BridgeSupport.Bool(payload, "confirm_execute"))
                throw new ArgumentException("confirm_execute=true is required");
            var commandId = Required(payload, "command_id");
            var before = FrameworkApplication.GetPlugInWrapper(commandId, true)
                ?? throw new InvalidOperationException($"ArcGIS Pro command not found: {commandId}");
            if (!before.Enabled || !before.IsRelevant)
                throw new InvalidOperationException(
                    $"ArcGIS Pro command is not currently executable: {commandId}");
            if (payload.TryGetProperty("arguments", out var args)
                && args.ValueKind == JsonValueKind.Array)
            {
                var execute = FrameworkApplication.ExecuteCommandArgs(commandId)
                    ?? throw new InvalidOperationException($"Command does not accept argument execution: {commandId}");
                await execute(args.EnumerateArray().Select(MaterializeJson).ToArray());
            }
            else
            {
                var execute = FrameworkApplication.ExecuteCommand(commandId)
                    ?? throw new InvalidOperationException($"Command cannot be executed: {commandId}");
                await execute();
            }
            var after = FrameworkApplication.GetPlugInWrapper(commandId, false);
            return new
            {
                command_id = commandId,
                executed = true,
                command_type = after?.CommandType.ToString() ?? before.CommandType.ToString(),
                enabled_after = after?.Enabled,
                checked_after = after?.Checked,
                source_data_modified = false,
                command_may_modify_project_or_external_state = true,
            };
        }, cancellationToken);
    }

    internal static async Task<object> ConfigureApplicationAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        return await OnUiAsync<object>(async () =>
        {
            if (!BridgeSupport.Bool(payload, "confirm_ui_change"))
                throw new ArgumentException("confirm_ui_change=true is required");
            if (payload.TryGetProperty("active_tab", out _))
                FrameworkApplication.ActivateTab(Required(payload, "active_tab"));
            if (payload.TryGetProperty("current_tool", out _))
                await FrameworkApplication.SetCurrentToolAsync(Required(payload, "current_tool"));
            if (payload.TryGetProperty("ribbon_minimized", out _))
                FrameworkApplication.IsRibbonMinimized = BridgeSupport.Bool(payload, "ribbon_minimized");
            if (BridgeSupport.Bool(payload, "close_backstage"))
                FrameworkApplication.CloseBackstage();
            if (payload.TryGetProperty("open_backstage_page", out _))
                FrameworkApplication.OpenBackstage(Required(payload, "open_backstage_page"));
            return DescribeState(default);
        }, cancellationToken);
    }

    internal static Task<object> ManagePaneAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        return OnUiAsync<object>(() =>
        {
            if (!BridgeSupport.Bool(payload, "confirm_pane_action"))
                throw new ArgumentException("confirm_pane_action=true is required");
            var operation = Required(payload, "operation").ToLowerInvariant();
            var paneId = BridgeSupport.String(payload, "pane_id");
            var instanceId = BridgeSupport.Int(payload, "instance_id", -1);
            object? result;
            if (operation == "create")
            {
                var pane = FrameworkApplication.Panes.Create(
                    paneId ?? throw new ArgumentException("pane_id is required for create"));
                result = DescribePane(pane);
            }
            else if (operation is "activate" or "hide" or "pin" or "unpin")
            {
                var dockPane = FrameworkApplication.DockPaneManager.Find(
                    paneId ?? throw new ArgumentException("pane_id is required"))
                    ?? throw new InvalidOperationException($"Dock pane not found: {paneId}");
                if (operation == "activate")
                    dockPane.Activate(BridgeSupport.Bool(payload, "force_activate", false));
                else if (operation == "hide")
                    dockPane.Hide();
                else if (operation == "pin")
                    dockPane.Pin();
                else
                    dockPane.UnPin();
                result = DescribeDockPane(dockPane);
            }
            else if (operation is "close" or "flash")
            {
                Pane? pane = instanceId >= 0
                    ? FrameworkApplication.Panes.FindPane((uint)instanceId)
                    : FrameworkApplication.Panes.Find(
                        paneId ?? throw new ArgumentException("pane_id or instance_id is required")).FirstOrDefault();
                if (pane is null)
                    throw new InvalidOperationException("Pane not found");
                if (operation == "close")
                    pane.Close();
                else
                    pane.Flash();
                result = DescribePane(pane);
            }
            else
            {
                throw new ArgumentException(
                    "operation must be create, activate, hide, pin, unpin, close, or flash");
            }
            return new
            {
                operation,
                pane_id = paneId,
                instance_id = instanceId >= 0 ? (int?)instanceId : null,
                pane = result,
                source_data_modified = false,
            };
        }, cancellationToken);
    }

    private static object DescribePane(Pane pane) => new
    {
        id = Safe(() => pane.ID),
        instance_id = Safe(() => pane.InstanceID),
        content_id = Safe(() => pane.ContentID),
        caption = Safe(() => pane.Caption),
        full_caption = Safe(() => pane.FullCaption),
        active = Safe(() => ReferenceEquals(FrameworkApplication.Panes.ActivePane, pane)),
        busy = Safe(() => pane.IsBusy),
        initialized = Safe(() => pane.Initialized),
        enabled = Safe(() => pane.Enabled),
        can_close = Safe(() => pane.CanClose),
        secondary = Safe(() => pane.Secondary),
    };

    private static object DescribeDockPane(DockPane pane) => new
    {
        id = Safe(() => pane.ID),
        caption = Safe(() => pane.Caption),
        tab_text = Safe(() => pane.TabText),
        visible = Safe(() => pane.IsVisible),
        floating = Safe(() => pane.IsFloating),
        dock_state = Safe(() => pane.DockState.ToString()),
        busy = Safe(() => pane.IsBusy),
        initialized = Safe(() => pane.Initialized),
        enabled = Safe(() => pane.Enabled),
    };

    private static object? MaterializeJson(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.Array => value.EnumerateArray().Select(MaterializeJson).ToArray(),
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(
                property => property.Name,
                property => MaterializeJson(property.Value)),
            _ => value.GetRawText(),
        };
    }

    private static T? Safe<T>(Func<T> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return default;
        }
    }

    private static string Required(JsonElement payload, string name)
    {
        return BridgeSupport.String(payload, name)
            ?? throw new ArgumentException($"{name} is required");
    }

    private static Dispatcher UiDispatcher => Application.Current?.Dispatcher
        ?? throw new InvalidOperationException("ArcGIS Pro UI dispatcher is unavailable");

    private static async Task<T> OnUiAsync<T>(
        Func<T> callback,
        CancellationToken cancellationToken)
    {
        var dispatcher = UiDispatcher;
        if (dispatcher.CheckAccess())
            return callback();
        return await dispatcher.InvokeAsync(
            callback, DispatcherPriority.Normal, cancellationToken);
    }

    private static async Task<T> OnUiAsync<T>(
        Func<Task<T>> callback,
        CancellationToken cancellationToken)
    {
        var dispatcher = UiDispatcher;
        if (dispatcher.CheckAccess())
            return await callback();
        var pending = await dispatcher.InvokeAsync(
            callback, DispatcherPriority.Normal, cancellationToken);
        return await pending;
    }
}
