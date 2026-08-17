using System.IO;
using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using ArcGIS.Desktop.Reports;

namespace ArcGISCodexBridgeAddin;

internal static class ReportActions
{
    internal static async Task<object> CreateAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var reportName = BridgeSupport.String(payload, "report_name")
            ?? throw new ArgumentException("report_name is required");
        var mapMemberName = BridgeSupport.String(payload, "map_member_name")
            ?? throw new ArgumentException("map_member_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var openReport = BridgeSupport.Bool(payload, "open_report", false);
        Report? createdReport = null;
        var result = await QueuedTask.Run<object>(() =>
        {
            if (Project.Current.GetItems<ReportProjectItem>().Any(item => string.Equals(
                item.Name, reportName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Report already exists: {reportName}");
            }
            var map = BridgeSupport.ResolveMap(requestedMap);
            var member = ResolveMapMember(map, mapMemberName);
            var descriptions = FieldDescriptions(member);
            var requestedFields = BridgeSupport.Strings(payload, "field_names");
            var selected = requestedFields.Length > 0
                ? requestedFields
                : descriptions.Where(field => field.IsVisible).Select(field => field.Name).ToArray();
            var unknown = selected.Where(name => descriptions.All(field => !string.Equals(
                field.Name, name, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (unknown.Length > 0)
            {
                throw new ArgumentException($"Unknown report fields: {string.Join(", ", unknown)}");
            }
            var groupFields = BridgeSupport.Strings(payload, "group_fields")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fields = selected.Select((name, index) =>
            {
                var description = descriptions.First(field => string.Equals(
                    field.Name, name, StringComparison.OrdinalIgnoreCase));
                return new CIMReportField
                {
                    Name = description.Name,
                    DisplayName = description.Alias,
                    IsVisible = true,
                    FieldOrder = index,
                    Group = groupFields.Contains(description.Name),
                    SortOrder = groupFields.Contains(description.Name) ? index : -1,
                };
            }).ToArray();
            if (BridgeSupport.Bool(payload, "use_selection", false))
            {
                throw new NotSupportedException(
                    "ArcGIS Pro 3.6 does not support selection-set filtering when creating a report. " +
                    "Use a definition_query or create the report from all records instead.");
            }
            var dataSource = new ReportDataSource(
                member,
                BridgeSupport.String(payload, "definition_query", string.Empty)!,
                fields);

            var statistics = ReadStatistics(payload, descriptions);
            var templateName = BridgeSupport.String(payload, "template_name");
            var stylingName = BridgeSupport.String(payload, "styling_name");
            Report report;
            if (!string.IsNullOrWhiteSpace(templateName)
                || !string.IsNullOrWhiteSpace(stylingName)
                || statistics.Length > 0
                || groupFields.Count > 0
                || payload.TryGetProperty("page_width", out _)
                || payload.TryGetProperty("page_height", out _))
            {
                var templates = ReportTemplateManager.GetTemplates();
                var template = !string.IsNullOrWhiteSpace(templateName)
                    ? templates.FirstOrDefault(item => string.Equals(
                        item.Name, templateName, StringComparison.OrdinalIgnoreCase))
                    : templates.FirstOrDefault(item => item.IsGrouping == (groupFields.Count > 0) && !item.IsCustom);
                template ??= templates.FirstOrDefault(item => !item.IsCustom);
                if (template is null)
                {
                    throw new InvalidOperationException("ArcGIS Pro has no built-in report template available.");
                }
                var stylings = ReportStylingManager.GetStylings();
                var styling = !string.IsNullOrWhiteSpace(stylingName)
                    ? stylings.FirstOrDefault(item => string.Equals(
                        item, stylingName, StringComparison.OrdinalIgnoreCase))
                    : stylings.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(styling))
                {
                    throw new InvalidOperationException("ArcGIS Pro has no report styling available.");
                }
                var page = new CIMPage
                {
                    Width = Math.Max(1, BridgeSupport.Double(payload, "page_width", 21.0)),
                    Height = Math.Max(1, BridgeSupport.Double(payload, "page_height", 29.7)),
                    Units = LinearUnit.Centimeters,
                };
                report = ReportFactory.Instance.CreateReport(
                    reportName, dataSource, page, statistics, template, styling);
            }
            else
            {
                report = ReportFactory.Instance.CreateReport(reportName, dataSource);
            }
            createdReport = report;
            return (object)new
            {
                report = report.Name,
                map = map.Name,
                map_member = member.Name,
                fields = selected,
                group_fields = groupFields.ToArray(),
                statistics = statistics.Select(item => new
                {
                    field = item.Field,
                    statistic = item.Statistic.ToString(),
                }).ToArray(),
                template = templateName,
                styling = stylingName,
                source_data_modified = false,
            };
        });
        if (openReport && createdReport is not null)
        {
            await await FrameworkApplication.Current.Dispatcher.InvokeAsync(
                () => FrameworkApplication.Panes.CreateReportPaneAsync(createdReport));
        }
        return result;
    }

    internal static async Task<object> ConfigureAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var reportName = BridgeSupport.String(payload, "report_name")
            ?? throw new ArgumentException("report_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            var report = ResolveReport(reportName);
            if (payload.TryGetProperty("definition_query", out _))
            {
                report.SetDefinitionQuery(BridgeSupport.String(payload, "definition_query", string.Empty)!);
            }
            var fieldNames = BridgeSupport.Strings(payload, "field_names");
            if (fieldNames.Length > 0)
            {
                var existing = report.DataSource.Fields.ToArray();
                var selected = fieldNames.Select((name, index) =>
                {
                    var field = existing.FirstOrDefault(item => string.Equals(
                        item.Name, name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"Report field not found: {name}");
                    var clone = field.Clone() as CIMReportField ?? field;
                    clone.IsVisible = true;
                    clone.FieldOrder = index;
                    return clone;
                }).ToArray();
                report.SetDataSourceFields(selected);
            }
            foreach (var fieldName in BridgeSupport.Strings(payload, "add_group_fields"))
            {
                report.AddGroup(
                    fieldName,
                    BridgeSupport.Bool(payload, "group_sort_ascending", true),
                    BridgeSupport.Bool(payload, "group_case_sensitive", false),
                    null);
            }
            var removeGroups = BridgeSupport.Strings(payload, "remove_group_fields");
            if (removeGroups.Length > 0)
            {
                report.RemoveGroups(removeGroups);
            }
            if (payload.TryGetProperty("page_width", out _)
                || payload.TryGetProperty("page_height", out _))
            {
                var page = report.Page.Clone() as CIMPage ?? report.Page;
                if (payload.TryGetProperty("page_width", out _))
                    page.Width = Math.Max(1, BridgeSupport.Double(payload, "page_width", page.Width));
                if (payload.TryGetProperty("page_height", out _))
                    page.Height = Math.Max(1, BridgeSupport.Double(payload, "page_height", page.Height));
                report.SetPage(page);
            }
            if (payload.TryGetProperty("sections", out var sections)
                && sections.ValueKind == JsonValueKind.Array)
            {
                foreach (var config in sections.EnumerateArray())
                {
                    var name = BridgeSupport.String(config, "name")
                        ?? throw new ArgumentException("Each report section configuration requires name");
                    var section = report.GetElementsAsFlattenedList().OfType<ReportSectionElement>()
                        .FirstOrDefault(item => string.Equals(
                            item.Name, name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Report section not found: {name}");
                    if (config.TryGetProperty("height", out _))
                        section.SetHeight(Math.Max(0.01, BridgeSupport.Double(config, "height")));
                    if (config.TryGetProperty("visible", out _))
                        section.SetVisible(BridgeSupport.Bool(config, "visible", true));
                    if (config.TryGetProperty("start_on_new_page", out _))
                    {
                        var definition = (CIMReportSectionElement)section.GetDefinition();
                        definition.StartOnNewPage = BridgeSupport.Bool(config, "start_on_new_page", false);
                        section.SetDefinition(definition);
                    }
                }
            }
            var removeSections = BridgeSupport.Strings(payload, "remove_sections");
            if (removeSections.Length > 0)
            {
                if (!BridgeSupport.Bool(payload, "confirm_remove_sections", false))
                {
                    throw new ArgumentException(
                        "confirm_remove_sections=true is required to remove report sections");
                }
                foreach (var sectionName in removeSections)
                {
                    report.RemoveSectionElement(sectionName);
                }
            }
            var sectionInventory = report.GetElementsAsFlattenedList().OfType<ReportSectionElement>()
                .Select(section =>
                {
                    var definition = (CIMReportSectionElement)section.GetDefinition();
                    var statistics = SafeStatistics(report, definition);
                    return new
                    {
                        name = section.Name,
                        section_type = section.GetType().Name,
                        visible = section.IsVisible,
                        height = section.GetHeight(),
                        start_on_new_page = definition.StartOnNewPage,
                        statistics = statistics.Select(item => new
                        {
                            field = item.Field,
                            statistic = item.Statistic.ToString(),
                        }).ToArray(),
                    };
                }).ToArray();
            return (object)new
            {
                report = report.Name,
                definition_query = report.DataSource.DefinitionQuery,
                fields = report.DataSource.Fields.Select(field => field.Name).ToArray(),
                page_width = report.Page.Width,
                page_height = report.Page.Height,
                sections = sectionInventory,
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> ListAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        return await QueuedTask.Run<object>(() =>
        {
            var reports = Project.Current.GetItems<ReportProjectItem>()
                .Select(item =>
                {
                    var report = item.GetReport();
                    return new
                    {
                        name = report.Name,
                        uri = report.URI,
                        connection_status = report.ConnectionStatus.ToString(),
                        element_count = report.GetElementsAsFlattenedList().Count,
                        elements = report.GetElementsAsFlattenedList().Select(element => new
                        {
                            name = element.Name,
                            type = element.GetType().Name,
                            visible = element.IsVisible,
                        }).ToArray(),
                        data_source_count = report.DataSources.Count,
                    };
                }).ToArray();
            return (object)new { reports };
        });
    }

    internal static async Task<object> DuplicateAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var reportName = BridgeSupport.String(payload, "report_name")
            ?? throw new ArgumentException("report_name is required");
        var newName = BridgeSupport.String(payload, "new_name")
            ?? throw new ArgumentException("new_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            if (Project.Current.GetItems<ReportProjectItem>()
                .Any(item => string.Equals(item.Name, newName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Report already exists: {newName}");
            }
            var source = ResolveReport(reportName);
            var copy = ReportFactory.Instance.CopyReport(source);
            copy.SetName(newName);
            return (object)new { source_report = source.Name, report = copy.Name };
        });
    }

    internal static async Task<object> RenameAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var reportName = BridgeSupport.String(payload, "report_name")
            ?? throw new ArgumentException("report_name is required");
        var newName = BridgeSupport.String(payload, "new_name")
            ?? throw new ArgumentException("new_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            var report = ResolveReport(reportName);
            report.SetName(newName);
            return (object)new { old_name = reportName, report = report.Name };
        });
    }

    internal static async Task<object> RemoveAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var reportName = BridgeSupport.String(payload, "report_name")
            ?? throw new ArgumentException("report_name is required");
        if (!BridgeSupport.Bool(payload, "confirm_remove", false))
        {
            throw new ArgumentException("confirm_remove=true is required to remove a report project item");
        }
        return await QueuedTask.Run<object>(() =>
        {
            var item = Project.Current.GetItems<ReportProjectItem>()
                .FirstOrDefault(candidate => string.Equals(
                    candidate.Name, reportName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Report not found: {reportName}");
            var removed = Project.Current.RemoveItem(item);
            return (object)new { report = reportName, removed_from_project = removed };
        });
    }

    internal static async Task<object> ExportPdfAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var reportName = BridgeSupport.String(payload, "report_name")
            ?? throw new ArgumentException("report_name is required");
        var output = BridgeSupport.String(payload, "output_file")
            ?? throw new ArgumentException("output_file is required");
        if (!output.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("output_file must end in .pdf");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? string.Empty);
        return await QueuedTask.Run<object>(() =>
        {
            var report = ResolveReport(reportName);
            var format = new PDFFormat
            {
                OutputFileName = output,
                Resolution = Math.Clamp(BridgeSupport.Int(payload, "dpi", 300), 72, 1200),
            };
            report.ExportToPDF(
                output,
                format,
                new ReportExportOptions(),
                BridgeSupport.Bool(payload, "use_selection", false));
            return (object)new
            {
                report = report.Name,
                output,
                dpi = format.Resolution,
            };
        });
    }

    private static Report ResolveReport(string reportName)
    {
        return Project.Current.GetItems<ReportProjectItem>()
            .FirstOrDefault(item => string.Equals(
                item.Name, reportName, StringComparison.OrdinalIgnoreCase))
            ?.GetReport()
            ?? throw new InvalidOperationException($"Report not found: {reportName}");
    }

    private static MapMember ResolveMapMember(Map map, string name)
    {
        return map.GetLayersAsFlattenedList().OfType<MapMember>()
            .Concat(map.StandaloneTables.Cast<MapMember>())
            .FirstOrDefault(member => string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Layer or standalone table not found: {name}");
    }

    private static IReadOnlyList<FieldDescription> FieldDescriptions(MapMember member)
    {
        return member switch
        {
            BasicFeatureLayer layer => layer.GetFieldDescriptions(),
            StandaloneTable table => table.GetFieldDescriptions(),
            _ => throw new InvalidOperationException(
                $"Map member does not expose report fields: {member.Name}"),
        };
    }

    private static ReportFieldStatistic[] ReadStatistics(
        JsonElement payload,
        IReadOnlyList<FieldDescription> fields)
    {
        if (!payload.TryGetProperty("statistics", out var statistics)
            || statistics.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ReportFieldStatistic>();
        }
        return statistics.EnumerateArray().Select(item =>
        {
            var fieldName = BridgeSupport.String(item, "field")
                ?? throw new ArgumentException("Each report statistic requires field");
            if (!fields.Any(field => string.Equals(
                field.Name, fieldName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"Report statistic field not found: {fieldName}");
            }
            if (!Enum.TryParse<FieldStatisticsFlag>(
                BridgeSupport.String(item, "statistic"), true, out var statistic)
                || statistic == FieldStatisticsFlag.NoStatistics)
            {
                throw new ArgumentException(
                    $"Unsupported report statistic: {BridgeSupport.String(item, "statistic")}");
            }
            return new ReportFieldStatistic { Field = fieldName, Statistic = statistic };
        }).ToArray();
    }

    private static IReadOnlyList<CIMReportElementFieldProperties> SafeStatistics(
        Report report,
        CIMReportSectionElement section)
    {
        try
        {
            return report.GetStatistics(section);
        }
        catch (ArgumentException)
        {
            return Array.Empty<CIMReportElementFieldProperties>();
        }
        catch (NotSupportedException)
        {
            return Array.Empty<CIMReportElementFieldProperties>();
        }
    }
}
