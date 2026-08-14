using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGIS.Desktop.Presentations;
using ArcGIS.Desktop.Reports;

namespace ArcGISCodexBridgeAddin;

internal static class DocumentElementFactoryActions
{
    internal static async Task<object> ManageReportAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var reportName = BridgeSupport.String(payload, "report_name")
            ?? throw new ArgumentException("report_name is required");
        var operation = (BridgeSupport.String(payload, "operation") ?? "inspect")
            .Trim().ToLowerInvariant();
        return await QueuedTask.Run<object>(() =>
        {
            var report = ResolveReport(reportName);
            if (operation == "inspect")
                return DescribeReport(report, false);

            if (operation == "remove")
            {
                if (!BridgeSupport.Bool(payload, "confirm_remove", false))
                    throw new ArgumentException("confirm_remove=true is required to remove report elements");
                var names = BridgeSupport.Strings(payload, "element_names");
                if (names.Length == 0)
                    throw new ArgumentException("element_names must contain at least one report element name");
                var elements = names.Select(name => report.FindElement(name)
                    ?? throw new InvalidOperationException($"Report element not found: {name}")).ToArray();
                report.DeleteElements(elements);
                return new
                {
                    operation,
                    report = report.Name,
                    removed = elements.Select(item => item.Name).ToArray(),
                    state = DescribeReport(report, true),
                    project_modified = true,
                    source_data_modified = false,
                    project_saved = false,
                };
            }

            if (operation != "add")
                throw new ArgumentException("operation must be inspect, add, or remove");

            var container = ResolveReportContainer(report, BridgeSupport.String(payload, "section_name"));
            var elementType = (BridgeSupport.String(payload, "element_type") ?? string.Empty)
                .Trim().ToLowerInvariant();
            var bounds = CheckedReportBounds(payload, container);
            var elementName = BridgeSupport.String(payload, "element_name");
            Element element = elementType switch
            {
                "paragraph" or "paragraph_text" => ReportElementFactory.Instance
                    .CreateRectangleParagraphGraphicElement(
                        container,
                        bounds,
                        BridgeSupport.String(payload, "text", string.Empty)!,
                        BridgeSupport.TextSymbol(payload)),
                "field_value" => ReportElementFactory.Instance.CreateFieldValueTextElement(
                    container,
                    bounds,
                    ResolveReportField(report, Required(payload, "field")),
                    BridgeSupport.TextSymbol(payload)),
                "field_statistic" => ReportElementFactory.Instance.CreateFieldStatisticTextElement(
                    container,
                    bounds,
                    new ReportFieldStatistic
                    {
                        Field = Required(payload, "field"),
                        Statistic = ParseStatistic(Required(payload, "statistic")),
                    },
                    BridgeSupport.TextSymbol(payload)),
                "dynamic_picture" => ReportElementFactory.Instance.CreateDynamicPictureGraphicElement(
                    container,
                    bounds,
                    Required(payload, "expression")),
                "attachment_frame" => ReportElementFactory.Instance.CreateAttachmentFrame(container, bounds),
                "chart_frame" => ReportElementFactory.Instance.CreateChartFrame(
                    container,
                    bounds,
                    ResolveMapMember(
                        BridgeSupport.ResolveMap(BridgeSupport.String(payload, "map_name")),
                        Required(payload, "map_member_name")),
                    Required(payload, "chart_name")),
                "map_frame" => ReportElementFactory.Instance.CreateMapFrame(
                    container,
                    bounds,
                    BridgeSupport.ResolveMap(BridgeSupport.String(payload, "map_name")),
                    elementName ?? "Report Map Frame"),
                _ => throw new ArgumentException(
                    "element_type must be paragraph, field_value, field_statistic, dynamic_picture, attachment_frame, chart_frame, or map_frame"),
            };
            if (!string.IsNullOrWhiteSpace(elementName))
                element.SetName(elementName);
            return new
            {
                operation,
                report = report.Name,
                section = (container as ReportSectionElement)?.Name,
                added = DescribeElement(element),
                state = DescribeReport(report, true),
                project_modified = true,
                source_data_modified = false,
                project_saved = false,
            };
        });
    }

    internal static async Task<object> ManagePresentationAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var presentationName = BridgeSupport.String(payload, "presentation_name")
            ?? throw new ArgumentException("presentation_name is required");
        var operation = (BridgeSupport.String(payload, "operation") ?? "inspect")
            .Trim().ToLowerInvariant();
        return await QueuedTask.Run<object>(() =>
        {
            var presentation = ResolvePresentation(presentationName);
            var pageIndex = BridgeSupport.Int(payload, "page_index", 0);
            if (pageIndex < 0 || pageIndex >= presentation.PageCount)
                throw new ArgumentOutOfRangeException("page_index");
            var page = presentation.GetPage(pageIndex);
            if (operation == "inspect")
                return DescribePresentationPage(presentation, page, pageIndex, false);

            if (operation == "remove")
            {
                if (!BridgeSupport.Bool(payload, "confirm_remove", false))
                    throw new ArgumentException("confirm_remove=true is required to remove presentation elements");
                var names = BridgeSupport.Strings(payload, "element_names");
                if (names.Length == 0)
                    throw new ArgumentException("element_names must contain at least one presentation element name");
                var elements = names.Select(name => page.FindElement(name)
                    ?? throw new InvalidOperationException($"Presentation element not found: {name}")).ToArray();
                page.DeleteElements(elements);
                return new
                {
                    operation,
                    presentation = presentation.Name,
                    page_index = pageIndex,
                    removed = elements.Select(item => item.Name).ToArray(),
                    state = DescribePresentationPage(presentation, page, pageIndex, true),
                    project_modified = true,
                    source_data_modified = false,
                    project_saved = false,
                };
            }

            if (operation == "group")
            {
                var names = BridgeSupport.Strings(payload, "element_names");
                if (names.Length < 2 || names.Length > 1000)
                    throw new ArgumentException("element_names must contain between 2 and 1000 elements to group");
                var elements = names.Select(name => page.FindElement(name)
                    ?? throw new InvalidOperationException($"Presentation element not found: {name}")).ToArray();
                var group = PresentationElementFactory.Instance.CreateGroupElement(
                    (IElementContainer)page,
                    elements,
                    BridgeSupport.String(payload, "element_name", "Codex Group")!,
                    false,
                    null);
                return MutationResult(presentation, page, pageIndex, operation, group);
            }

            if (operation == "ungroup")
            {
                var groupName = Required(payload, "element_name");
                var group = page.FindElement(groupName) as GroupElement
                    ?? throw new InvalidOperationException($"Presentation group element not found: {groupName}");
                page.UnGroupElement(group);
                return new
                {
                    operation,
                    presentation = presentation.Name,
                    page_index = pageIndex,
                    ungrouped = groupName,
                    state = DescribePresentationPage(presentation, page, pageIndex, true),
                    project_modified = true,
                    source_data_modified = false,
                    project_saved = false,
                };
            }

            if (operation != "add")
                throw new ArgumentException("operation must be inspect, add, remove, group, or ungroup");

            var container = (IElementContainer)page;
            var type = (BridgeSupport.String(payload, "element_type") ?? string.Empty)
                .Trim().ToLowerInvariant();
            var name = BridgeSupport.String(payload, "element_name", $"Codex {type}")!;
            var bounds = CheckedBounds(payload, "bounds", 0.5, 0.5, 4.0, 2.0);
            Element added = type switch
            {
                "text" => PresentationElementFactory.Instance.CreateTextGraphicElement(
                    container,
                    TextType.RectangleParagraph,
                    bounds,
                    BridgeSupport.TextSymbol(payload),
                    BridgeSupport.String(payload, "text", string.Empty)!,
                    name,
                    false,
                    null),
                "picture" => PresentationElementFactory.Instance.CreatePictureGraphicElement(
                    container,
                    bounds,
                    Required(payload, "media_path"),
                    name,
                    false,
                    null),
                "shape" => PresentationElementFactory.Instance.CreatePredefinedShapeGraphicElement(
                    container,
                    ParseShape(BridgeSupport.String(payload, "shape", "Rectangle")),
                    bounds,
                    PolygonSymbol(payload),
                    name,
                    false,
                    null),
                "arrow" => PresentationElementFactory.Instance.CreateArrowGraphicElement(
                    container,
                    ArrowLine(payload),
                    new ArrowInfo
                    {
                        ArrowOnBothEnds = BridgeSupport.Bool(payload, "arrow_on_both_ends", false),
                        ArrowSizePoints = Math.Clamp(BridgeSupport.Double(payload, "arrow_size", 8), 0.1, 100),
                        LineWidthPoints = Math.Clamp(BridgeSupport.Double(payload, "line_width", 1.5), 0.1, 100),
                        ArrowHeadKey = BridgeSupport.String(payload, "arrow_head_key"),
                    },
                    name,
                    false,
                    null),
                _ => throw new ArgumentException("element_type must be text, picture, shape, or arrow"),
            };
            return MutationResult(presentation, page, pageIndex, operation, added);
        });
    }

    private static object MutationResult(
        Presentation presentation,
        PresentationPage page,
        int pageIndex,
        string operation,
        Element element)
    {
        return new
        {
            operation,
            presentation = presentation.Name,
            page_index = pageIndex,
            added = DescribeElement(element),
            state = DescribePresentationPage(presentation, page, pageIndex, true),
            project_modified = true,
            source_data_modified = false,
            project_saved = false,
        };
    }

    private static object DescribeReport(Report report, bool modified) => new
    {
        report = report.Name,
        active_section = report.ActiveSection?.Name,
        writable_sections = WritableReportSections(report).Select(section => new
        {
            name = section.Name,
            type = section.GetType().Name,
            width = Finite(section.GetWidth()),
            height = Finite(section.GetHeight()),
        }).ToArray(),
        elements = report.GetElementsAsFlattenedList().Select(DescribeElement).ToArray(),
        project_modified = modified,
        source_data_modified = false,
        project_saved = false,
    };

    private static object DescribePresentationPage(
        Presentation presentation,
        PresentationPage page,
        int pageIndex,
        bool modified) => new
    {
        presentation = presentation.Name,
        page_index = pageIndex,
        page_type = page.PageType.ToString(),
        elements = page.GetFlattenedElements().Select(DescribeElement).ToArray(),
        project_modified = modified,
        source_data_modified = false,
        project_saved = false,
    };

    private static object DescribeElement(Element element) => new
    {
        name = element.Name,
        type = element.GetType().Name,
        visible = element.IsVisible,
        locked = element.IsLocked,
        x = Finite(element.GetX()),
        y = Finite(element.GetY()),
        width = Finite(element.GetWidth()),
        height = Finite(element.GetHeight()),
        rotation = Finite(element.GetRotation()),
    };

    private static ReportSectionElement ResolveReportContainer(Report report, string? sectionName)
    {
        var writable = WritableReportSections(report);
        if (string.IsNullOrWhiteSpace(sectionName))
        {
            if (report.ActiveSection is ReportSectionElement active
                && active.GetType() != typeof(ReportSection))
                return active;
            return writable
                .OrderByDescending(item => item is ReportHeader)
                .ThenByDescending(item => item.GetHeight())
                .FirstOrDefault()
                ?? throw new InvalidOperationException("Report has no writable child section");
        }

        var section = report.GetElementsAsFlattenedList().OfType<ReportSectionElement>()
            .FirstOrDefault(item => string.Equals(item.Name, sectionName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Report section not found: {sectionName}");
        if (section.GetType() == typeof(ReportSection))
            throw new InvalidOperationException(
                $"Report section '{sectionName}' is the root report container and cannot contain report elements. "
                + $"Writable sections: {WritableSectionSummary(writable)}");
        return section;
    }

    private static ReportSectionElement[] WritableReportSections(Report report) => report
        .GetElementsAsFlattenedList()
        .OfType<ReportSectionElement>()
        .Where(section => section.GetType() != typeof(ReportSection))
        .ToArray();

    private static string WritableSectionSummary(IEnumerable<ReportSectionElement> sections)
    {
        var values = sections.Select(section =>
            $"{section.Name} ({section.GetType().Name}, {Finite(section.GetWidth())} x {Finite(section.GetHeight())})")
            .ToArray();
        return values.Length == 0 ? "none" : string.Join(", ", values);
    }

    private static Report ResolveReport(string name) => Project.Current.GetItems<ReportProjectItem>()
        .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
        ?.GetReport() ?? throw new InvalidOperationException($"Report not found: {name}");

    private static Presentation ResolvePresentation(string name) => Project.Current
        .GetItems<PresentationProjectItem>()
        .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
        ?.GetPresentation() ?? throw new InvalidOperationException($"Presentation not found: {name}");

    private static CIMReportField ResolveReportField(Report report, string name)
    {
        return report.DataSource.Fields.FirstOrDefault(item => string.Equals(
            item.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Report field not found: {name}");
    }

    private static MapMember ResolveMapMember(Map map, string name)
    {
        return map.GetLayersAsFlattenedList().OfType<MapMember>()
            .Concat(map.StandaloneTables.Cast<MapMember>())
            .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Map member not found: {name}");
    }

    private static FieldStatisticsFlag ParseStatistic(string text)
    {
        if (!Enum.TryParse<FieldStatisticsFlag>(text, true, out var result)
            || result == FieldStatisticsFlag.NoStatistics)
            throw new ArgumentException($"Unsupported report statistic: {text}");
        return result;
    }

    private static PredefinedShape ParseShape(string? text)
    {
        return Enum.TryParse<PredefinedShape>(text, true, out var shape)
            && shape != PredefinedShape.None
            ? shape
            : PredefinedShape.Rectangle;
    }

    private static CIMPolygonSymbol PolygonSymbol(JsonElement payload)
    {
        return SymbolFactory.Instance.ConstructPolygonSymbol(
            BridgeSupport.Color(payload, "fill_color", ColorFactory.Instance.CreateRGBColor(70, 125, 180, 45)),
            SimpleFillStyle.Solid,
            SymbolFactory.Instance.ConstructStroke(
                BridgeSupport.Color(payload, "outline_color", ColorFactory.Instance.CreateRGBColor(45, 45, 45)),
                Math.Clamp(BridgeSupport.Double(payload, "outline_width", 1), 0, 100),
                SimpleLineStyle.Solid));
    }

    private static Polyline ArrowLine(JsonElement payload)
    {
        if (!payload.TryGetProperty("start", out var start)
            || !payload.TryGetProperty("end", out var end))
            throw new ArgumentException("start and end objects are required for an arrow");
        var x1 = RequiredFinite(start, "x", "start");
        var y1 = RequiredFinite(start, "y", "start");
        var x2 = RequiredFinite(end, "x", "end");
        var y2 = RequiredFinite(end, "y", "end");
        return PolylineBuilderEx.CreatePolyline(new[]
        {
            MapPointBuilderEx.CreateMapPoint(x1, y1),
            MapPointBuilderEx.CreateMapPoint(x2, y2),
        });
    }

    private static Envelope CheckedBounds(
        JsonElement payload,
        string property,
        double xmin,
        double ymin,
        double xmax,
        double ymax)
    {
        var result = BridgeSupport.Bounds(payload, property, xmin, ymin, xmax, ymax);
        if (!double.IsFinite(result.XMin) || !double.IsFinite(result.YMin)
            || !double.IsFinite(result.XMax) || !double.IsFinite(result.YMax)
            || result.Width <= 0 || result.Height <= 0
            || result.Width > 10000 || result.Height > 10000)
            throw new ArgumentException($"{property} must be finite, positive, and bounded");
        return result;
    }

    private static Envelope CheckedReportBounds(
        JsonElement payload,
        ReportSectionElement section)
    {
        var sectionX = section.GetX();
        var sectionY = section.GetY();
        var width = section.GetWidth();
        var height = section.GetHeight();
        var xPadding = Math.Min(0.5, width * 0.1);
        var yPadding = Math.Min(0.2, height * 0.1);
        var defaultXMin = sectionX + xPadding;
        var defaultYMin = sectionY + yPadding;
        var defaultXMax = Math.Max(defaultXMin + 0.01, sectionX + width - xPadding);
        var defaultYMax = Math.Min(
            sectionY + height - yPadding,
            Math.Max(defaultYMin + 0.01, defaultYMin + Math.Min(1.0, height * 0.5)));
        var result = CheckedBounds(
            payload,
            "bounds",
            defaultXMin,
            defaultYMin,
            defaultXMax,
            defaultYMax);
        const double tolerance = 0.00001;
        if (result.XMin < sectionX - tolerance || result.YMin < sectionY - tolerance
            || result.XMax > sectionX + width + tolerance
            || result.YMax > sectionY + height + tolerance)
        {
            throw new ArgumentException(
                $"bounds must fit inside report section '{section.Name}' "
                + $"({section.GetType().Name}, xmin={Finite(sectionX)}, ymin={Finite(sectionY)}, "
                + $"xmax={Finite(sectionX + width)}, ymax={Finite(sectionY + height)}). "
                + $"Received xmin={Finite(result.XMin)}, ymin={Finite(result.YMin)}, "
                + $"xmax={Finite(result.XMax)}, ymax={Finite(result.YMax)}");
        }
        return result;
    }

    private static string Required(JsonElement payload, string name)
    {
        return BridgeSupport.String(payload, name)
            ?? throw new ArgumentException($"{name} is required");
    }

    private static double RequiredFinite(JsonElement payload, string name, string prefix)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            throw new ArgumentException($"{prefix}.{name} is required and must be numeric");
        var result = value.GetDouble();
        if (!double.IsFinite(result))
            throw new ArgumentException($"{prefix}.{name} must be finite");
        return result;
    }

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
}
