using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Presentations;
using ArcGIS.Desktop.Reports;

namespace ArcGISCodexBridgeAddin;

internal static class AdvancedDocumentActions
{
    internal static async Task<object> ManageReportCompositionAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var reportName = Required(payload, "report_name");
        var operation = (BridgeSupport.String(payload, "operation") ?? "inspect")
            .Trim().ToLowerInvariant();
        return await QueuedTask.Run<object>(() =>
        {
            var report = ResolveReport(reportName);
            if (operation == "inspect")
                return DescribeReport(report, false);

            if (operation == "add_layout_page")
            {
                var item = ResolveLayoutItem(Required(payload, "layout_name"));
                var section = BridgeSupport.String(payload, "section_name");
                if (!string.IsNullOrWhiteSpace(section))
                    report.AddLayoutPage(item, section);
                else
                    report.AddLayoutPage(item, Math.Max(0, BridgeSupport.Int(payload, "insert_index", 0)));
                return Result(report, operation);
            }

            if (operation == "replace_layout_page")
            {
                report.ReplaceLayoutPage(
                    ResolveLayoutItem(Required(payload, "layout_name")),
                    Required(payload, "supplemental_page_name"));
                return Result(report, operation);
            }

            if (operation == "add_subreport")
            {
                var item = ResolveReportItem(Required(payload, "subreport_name"));
                report.AddSubReport(
                    item,
                    Math.Max(0, BridgeSupport.Int(payload, "insert_index", 0)),
                    BridgeSupport.Bool(payload, "adjust_page_size", true));
                return Result(report, operation);
            }

            if (operation == "set_expressions")
            {
                var section = Required(payload, "section_name");
                if (!payload.TryGetProperty("expressions", out var values)
                    || values.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException("expressions array is required");
                var expressions = values.EnumerateArray().Take(500).Select(item =>
                {
                    var returnType = Enum.TryParse<ExpressionReturnType>(
                        BridgeSupport.String(item, "return_type", "String"), true, out var parsed)
                        ? parsed : ExpressionReturnType.String;
                    return new CIMExpressionInfo
                    {
                        Name = Required(item, "name"),
                        Title = BridgeSupport.String(item, "title", Required(item, "name")),
                        Expression = Required(item, "expression"),
                        ReturnType = returnType,
                    };
                }).ToArray();
                report.SetExpressions(
                    expressions,
                    section,
                    BridgeSupport.Strings(payload, "old_expression_names"),
                    BridgeSupport.Strings(payload, "renamed_expression_names"));
                return Result(report, operation);
            }

            if (operation == "remove_supplemental_page")
            {
                RequireRemoval(payload);
                report.RemoveSupplementalPage(Required(payload, "supplemental_page_name"));
                return Result(report, operation);
            }

            if (operation == "remove_related_report")
            {
                RequireRemoval(payload);
                report.RemoveRelatedReport(Required(payload, "related_report_name"));
                return Result(report, operation);
            }

            throw new ArgumentException(
                "operation must be inspect, add_layout_page, replace_layout_page, add_subreport, set_expressions, remove_supplemental_page, or remove_related_report");
        });
    }

    internal static async Task<object> ConfigurePresentationTransitionAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var presentationName = Required(payload, "presentation_name");
        var operation = (BridgeSupport.String(payload, "operation") ?? "inspect")
            .Trim().ToLowerInvariant();
        return await QueuedTask.Run<object>(() =>
        {
            var presentation = ResolvePresentation(presentationName);
            var indices = ResolvePageIndices(payload, presentation.PageCount);
            if (operation == "inspect")
                return DescribePages(presentation, indices, false);
            if (operation is not "apply" and not "clear")
                throw new ArgumentException("operation must be inspect, apply, or clear");

            var transitionType = operation == "clear"
                ? PresentationTransitionType.None
                : ParseTransition(BridgeSupport.String(payload, "transition_type", "Fade"));
            var swipe = ParseSwipe(BridgeSupport.String(payload, "swipe_direction", "None"));
            var duration = Math.Clamp(BridgeSupport.Double(payload, "duration", 1), 0, 120);
            foreach (var index in indices)
            {
                var page = presentation.GetPage(index);
                page.SetTransition(new CIMPresentationTransition
                {
                    TransitionType = transitionType,
                    SwipeDirection = transitionType == PresentationTransitionType.Swipe
                        ? swipe : SwipeDirection.None,
                    Duration = duration,
                });
                if (payload.TryGetProperty("speaker_notes", out _))
                {
                    var definition = page.GetDefinition();
                    definition.SpeakerNotes = BridgeSupport.String(payload, "speaker_notes", string.Empty);
                    page.SetDefinition(definition);
                }
            }
            return DescribePages(presentation, indices, true);
        });
    }

    private static object Result(Report report, string operation) => new
    {
        operation,
        state = DescribeReport(report, true),
        project_modified = true,
        source_data_modified = false,
        project_saved = false,
    };

    private static object DescribeReport(Report report, bool modified)
    {
        var sections = report.GetElementsAsFlattenedList().OfType<ReportSectionElement>()
            .Take(1000)
            .Select(section => new
            {
                name = section.Name,
                type = section.GetType().Name,
                visible = section.IsVisible,
                expressions = SafeExpressions(report, section.Name).Take(500).Select(item => new
                {
                    name = item.Name,
                    title = item.Title,
                    expression = item.Expression,
                    return_type = item.ReturnType.ToString(),
                }).ToArray(),
            }).ToArray();
        return new
        {
            report = report.Name,
            sections,
            element_count = report.GetElementsAsFlattenedList().Count,
            project_modified = modified,
            source_data_modified = false,
            project_saved = false,
        };
    }

    private static object DescribePages(Presentation presentation, int[] indices, bool modified)
    {
        return new
        {
            presentation = presentation.Name,
            pages = indices.Select(index =>
            {
                var page = presentation.GetPage(index);
                var transition = page.Transition;
                return new
                {
                    page_index = index,
                    page_type = page.PageType.ToString(),
                    transition_type = transition?.TransitionType.ToString(),
                    swipe_direction = transition?.SwipeDirection.ToString(),
                    duration = transition is null || !double.IsFinite(transition.Duration)
                        ? null : (double?)transition.Duration,
                    speaker_notes = page.GetDefinition().SpeakerNotes,
                };
            }).ToArray(),
            project_modified = modified,
            source_data_modified = false,
            project_saved = false,
        };
    }

    private static IReadOnlyList<CIMExpressionInfo> SafeExpressions(Report report, string sectionName)
    {
        try
        {
            return report.GetExpressions(sectionName);
        }
        catch
        {
            return Array.Empty<CIMExpressionInfo>();
        }
    }

    private static int[] ResolvePageIndices(JsonElement payload, int pageCount)
    {
        var indices = payload.TryGetProperty("page_indices", out var values)
            && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Take(1000).Select(item => item.GetInt32()).Distinct().ToArray()
            : new[] { BridgeSupport.Int(payload, "page_index", 0) };
        if (indices.Length == 0 || indices.Any(index => index < 0 || index >= pageCount))
            throw new ArgumentOutOfRangeException("page_indices");
        return indices;
    }

    private static PresentationTransitionType ParseTransition(string? value)
    {
        return Enum.TryParse<PresentationTransitionType>(value, true, out var result)
            ? result : throw new ArgumentException($"Unsupported presentation transition: {value}");
    }

    private static SwipeDirection ParseSwipe(string? value)
    {
        return Enum.TryParse<SwipeDirection>(value, true, out var result)
            ? result : throw new ArgumentException($"Unsupported swipe direction: {value}");
    }

    private static void RequireRemoval(JsonElement payload)
    {
        if (!BridgeSupport.Bool(payload, "confirm_remove", false))
            throw new ArgumentException("confirm_remove=true is required for report composition removal");
    }

    private static Report ResolveReport(string name) => ResolveReportItem(name).GetReport();

    private static ReportProjectItem ResolveReportItem(string name) => Project.Current
        .GetItems<ReportProjectItem>()
        .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Report not found: {name}");

    private static LayoutProjectItem ResolveLayoutItem(string name) => Project.Current
        .GetItems<LayoutProjectItem>()
        .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Layout not found: {name}");

    private static Presentation ResolvePresentation(string name) => Project.Current
        .GetItems<PresentationProjectItem>()
        .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
        ?.GetPresentation() ?? throw new InvalidOperationException($"Presentation not found: {name}");

    private static string Required(JsonElement payload, string name) => BridgeSupport.String(payload, name)
        ?? throw new ArgumentException($"{name} is required");
}
