using System.Text.Json;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.TaskAssistant;
using ArcGIS.Desktop.Workflow.Client;
using ArcGIS.Desktop.Workflow.Client.Models;

namespace ArcGISCodexBridgeAddin;

internal static class TaskWorkflowActions
{
    internal static async Task<object> ListTasksAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var items = await QueuedTask.Run(() => Project.Current.GetItems<TaskProjectItem>().ToArray());
        var results = new List<object>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskItemInfo? info = null;
            string? error = null;
            try
            {
                info = await item.GetTaskItemInfoAsync();
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }
            results.Add(new
            {
                name = item.Name,
                path = item.Path,
                task_item_guid = item.TaskItemGuid,
                is_open = item.IsOpen,
                description = info?.Description,
                info_guid = info?.Guid,
                tasks = info?.GetTasks().Select(task => new
                {
                    name = task.Name,
                    guid = task.Guid,
                    group_path = task.GroupPath,
                }).ToArray() ?? Array.Empty<object>(),
                error,
            });
        }
        return new
        {
            task_items = results,
            count = results.Count,
            source_data_modified = false,
        };
    }

    internal static async Task<object> ManageTaskAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var operation = Required(payload, "operation").ToLowerInvariant();
        var factory = TaskAssistantFactory.Instance;
        Guid taskItemGuid;
        Guid? taskGuid = OptionalGuid(payload, "task_guid");
        string? output = null;
        switch (operation)
        {
            case "open_item":
                taskItemGuid = await ResolveTaskItemGuidAsync(payload);
                await factory.OpenTaskItemAsync(taskItemGuid);
                break;
            case "open_task":
                taskItemGuid = await ResolveTaskItemGuidAsync(payload);
                if (taskGuid is null)
                    throw new ArgumentException("task_guid is required for open_task");
                await factory.OpenTaskItemAsync(taskItemGuid, taskGuid.Value);
                break;
            case "close_item":
                taskItemGuid = await ResolveTaskItemGuidAsync(payload);
                await factory.CloseTaskItemAsync(taskItemGuid);
                break;
            case "import_file":
                taskItemGuid = await factory.ImportTaskFileAsync(Required(payload, "task_file"));
                break;
            case "open_file":
                taskItemGuid = taskGuid is null
                    ? await factory.OpenTaskFileAsync(Required(payload, "task_file"))
                    : await factory.OpenTaskFileAsync(Required(payload, "task_file"), taskGuid.Value);
                break;
            case "export_item":
                taskItemGuid = await ResolveTaskItemGuidAsync(payload);
                output = await factory.ExportTaskItemAsync(taskItemGuid, Required(payload, "output_file"));
                break;
            default:
                throw new ArgumentException(
                    "operation must be open_item, open_task, close_item, import_file, open_file, or export_item");
        }
        return new
        {
            operation,
            task_item_guid = taskItemGuid,
            task_guid = taskGuid,
            output,
            source_data_modified = false,
        };
    }

    internal static Task<object> GetWorkflowStatusAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var module = ResolveWorkflowModule(false);
        if (module is null)
        {
            return Task.FromResult<object>(new
            {
                available = false,
                connected = false,
                source_data_modified = false,
            });
        }
        string? currentJobId = null;
        if (WorkflowClientModule.IsConnected)
        {
            try
            {
                currentJobId = WorkflowClientModule.JobsManager.GetJobId();
            }
            catch
            {
                // A connected item can still have no current job.
            }
        }
        return Task.FromResult<object>(new
        {
            available = true,
            connected = WorkflowClientModule.IsConnected,
            server_url = WorkflowClientModule.ServerUrl,
            item_id = WorkflowClientModule.ItemId,
            current_job_id = currentJobId,
            notification_subscriber_id = WorkflowClientModule.IsConnected
                ? WorkflowClientModule.NotificationManager.SubscriberId
                : null,
            source_data_modified = false,
        });
    }

    internal static Task<object> SearchWorkflowJobsAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var module = ResolveWorkflowModule(true)!;
        var fields = BridgeSupport.Strings(payload, "fields").ToList();
        var query = new SearchQuery
        {
            Q = BridgeSupport.String(payload, "q"),
            Search = BridgeSupport.String(payload, "search"),
            Start = Math.Max(0, BridgeSupport.Int(payload, "start", 0)),
            Num = Math.Clamp(BridgeSupport.Int(payload, "num", 50), 1, 200),
            Fields = fields,
            SortFields = ParseSortFields(payload),
        };
        var result = WorkflowClientModule.JobsManager.SearchJobs(query);
        return Task.FromResult<object>(new
        {
            query = result.Q,
            start = result.Start,
            next_start = result.NextStart,
            count = result.Num,
            fields = result.Fields,
            results = result.Results.Take(query.Num).ToArray(),
            source_data_modified = false,
        });
    }

    internal static Task<object> GetWorkflowJobAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var module = ResolveWorkflowModule(true)!;
        var job = WorkflowClientModule.JobsManager.GetJob(
            Required(payload, "job_id"),
            BridgeSupport.Bool(payload, "include_extended_properties", false),
            BridgeSupport.Bool(payload, "include_related_properties", false));
        return Task.FromResult(DescribeJob(job));
    }

    internal static Task<object> ControlWorkflowJobAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!BridgeSupport.Bool(payload, "confirm_workflow_action"))
            throw new ArgumentException("confirm_workflow_action=true is required");
        var module = ResolveWorkflowModule(true)!;
        var operation = Required(payload, "operation").ToLowerInvariant();
        var jobId = Required(payload, "job_id");
        var stepIds = BridgeSupport.Strings(payload, "step_ids").ToList();
        switch (operation)
        {
            case "set_current_step":
                WorkflowClientModule.JobsManager.SetCurrentStep(jobId, Required(payload, "step_id"));
                break;
            case "run_steps":
                RequireStepIds(stepIds, operation);
                WorkflowClientModule.JobsManager.RunSteps(jobId, stepIds);
                break;
            case "stop_steps":
                RequireStepIds(stepIds, operation);
                WorkflowClientModule.JobsManager.StopSteps(jobId, stepIds);
                break;
            case "finish_steps":
                RequireStepIds(stepIds, operation);
                WorkflowClientModule.JobsManager.FinishSteps(jobId, stepIds);
                break;
            case "assign_step":
            {
                var raw = Required(payload, "assigned_type");
                if (!Enum.TryParse<AssignedType>(raw, true, out var assignedType))
                    throw new ArgumentException($"Unsupported assigned_type: {raw}");
                WorkflowClientModule.JobsManager.AssignStep(
                    jobId,
                    Required(payload, "step_id"),
                    assignedType,
                    Required(payload, "assigned_to"));
                break;
            }
            default:
                throw new ArgumentException(
                    "operation must be set_current_step, run_steps, stop_steps, finish_steps, or assign_step");
        }
        return Task.FromResult<object>(new
        {
            operation,
            job_id = jobId,
            step_id = BridgeSupport.String(payload, "step_id"),
            step_ids = stepIds,
            external_workflow_state_modified = true,
            source_data_modified = false,
        });
    }

    private static object DescribeJob(Job job)
    {
        return new
        {
            job_id = job.JobId,
            name = job.JobName,
            description = job.Description,
            status = job.JobStatus,
            template_id = job.JobTemplateId,
            template_name = job.JobTemplateName,
            owned_by = job.OwnedBy,
            priority = job.Priority,
            parent_job_id = job.ParentJobId,
            start_date = job.StartDate,
            due_date = job.DueDate,
            notes = job.Notes,
            current_steps = job.CurrentSteps?.Select(step => new
            {
                step_id = step.StepId,
                name = step.StepName,
                assigned_to = step.AssignedTo,
                assigned_type = step.AssignedType.ToString(),
                can_skip = step.CanSkip,
                dependencies = step.Dependencies,
                manual_hold = step.ManualHold,
                hold_scheduled_release = step.HoldScheduledRelease,
            }).ToArray() ?? Array.Empty<object>(),
            holds = job.Holds,
            active_versions = job.ActiveVersions,
            extended_properties = job.ExtendedProperties,
            related_properties = job.RelatedProperties,
            source_data_modified = false,
        };
    }

    private static WorkflowClientModule? ResolveWorkflowModule(bool requireConnected)
    {
        var module = FrameworkApplication.FindModule("workflow_client_module") as WorkflowClientModule;
        if (module is null)
        {
            if (requireConnected)
                throw new InvalidOperationException("Workflow Client module is not available");
            return null;
        }
        if (requireConnected && !WorkflowClientModule.IsConnected)
            throw new InvalidOperationException("Workflow Manager is not connected");
        return module;
    }

    private static List<SortField> ParseSortFields(JsonElement payload)
    {
        var result = new List<SortField>();
        if (!payload.TryGetProperty("sort_fields", out var values)
            || values.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var item in values.EnumerateArray())
        {
            var direction = BridgeSupport.String(item, "order", "Asc");
            if (!Enum.TryParse<SortOrder>(direction, true, out var order))
                throw new ArgumentException($"Unsupported sort order: {direction}");
            result.Add(new SortField
            {
                FieldName = Required(item, "field"),
                SortOrder = order,
            });
        }
        return result;
    }

    private static void RequireStepIds(List<string> stepIds, string operation)
    {
        if (stepIds.Count == 0)
            throw new ArgumentException($"step_ids is required for {operation}");
    }

    private static async Task<Guid> ResolveTaskItemGuidAsync(JsonElement payload)
    {
        var explicitGuid = OptionalGuid(payload, "task_item_guid");
        if (explicitGuid is not null)
            return explicitGuid.Value;
        var name = Required(payload, "task_item_name");
        return await QueuedTask.Run(() => Project.Current.GetItems<TaskProjectItem>()
            .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.TaskItemGuid
            ?? throw new InvalidOperationException($"Task item not found: {name}"));
    }

    private static Guid? OptionalGuid(JsonElement payload, string name)
    {
        var raw = BridgeSupport.String(payload, name);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (!Guid.TryParse(raw, out var result))
            throw new ArgumentException($"{name} must be a GUID");
        return result;
    }

    private static string Required(JsonElement payload, string name)
    {
        return BridgeSupport.String(payload, name)
            ?? throw new ArgumentException($"{name} is required");
    }
}
