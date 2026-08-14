using System.Reflection;
using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGIS.Desktop.Presentations;
using ArcGIS.Desktop.Reports;

namespace ArcGISCodexBridgeAddin;

/// <summary>
/// Generic CIM round-trip actions for project objects whose public SDK surface
/// exposes GetDefinition/SetDefinition. These actions intentionally preserve
/// identity, source connections, membership and other structural links by
/// default so MCP-side transforms can focus on display and authoring details.
/// </summary>
internal static class CimObjectActions
{
    internal static async Task<object> GetLayoutElementAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = Required(payload, "layout_name");
        var elementName = Required(payload, "element_name");
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var element = ResolveElement(layout, elementName);
            return DescribeElement("layout", layout.Name, element);
        });
    }

    internal static async Task<object> SetLayoutElementAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        RequireConfirmation(payload);
        var layoutName = Required(payload, "layout_name");
        var elementName = Required(payload, "element_name");
        var cimJson = Required(payload, "cim_json");
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var element = ResolveElement(layout, elementName);
            var current = element.GetDefinition();
            var replacement = ParseSameType(current, cimJson);
            PreserveElementLinks(current, replacement, payload);
            element.SetDefinition(replacement);
            return DescribeElement("layout", layout.Name, element, replaced: true);
        });
    }

    internal static async Task<object> CreateLayoutElementAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = Required(payload, "layout_name");
        var cimType = Required(payload, "cim_type");
        var cimJson = Required(payload, "cim_json");
        var requestedName = BridgeSupport.String(payload, "element_name");
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var definition = ParseNamedType<CIMElement>(cimType, cimJson);
            if (!string.IsNullOrWhiteSpace(requestedName))
            {
                definition.Name = requestedName;
            }
            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                throw new ArgumentException("The CIM element requires a name or element_name override");
            }
            if (layout.GetElementsAsFlattenedList().Any(item => string.Equals(
                item.Name, definition.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Layout element already exists: {definition.Name}");
            }
            var element = ElementFactory.Instance.CreateElement(layout, definition, false);
            return DescribeElement("layout", layout.Name, element, created: true);
        });
    }

    internal static async Task<object> GetStandaloneTableAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var tableName = Required(payload, "table_name");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var table = ResolveStandaloneTable(map, tableName);
            var definition = table.GetDefinition();
            return (object)new
            {
                map = map.Name,
                table = table.Name,
                cim_type = definition.GetType().Name,
                cim_json = definition.ToJson(),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> SetStandaloneTableAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        RequireConfirmation(payload);
        var tableName = Required(payload, "table_name");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var cimJson = Required(payload, "cim_json");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var table = ResolveStandaloneTable(map, tableName);
            var current = table.GetDefinition();
            var replacement = ParseSameType(current, cimJson);
            if (BridgeSupport.Bool(payload, "preserve_name", true))
            {
                replacement.Name = current.Name;
            }
            if (BridgeSupport.Bool(payload, "preserve_source_connection", true))
            {
                replacement.URI = current.URI;
                replacement.SourceURI = current.SourceURI;
                replacement.SourcePortalUrl = current.SourcePortalUrl;
                replacement.DataConnection = current.DataConnection;
                replacement.SelectionSetURI = current.SelectionSetURI;
            }
            table.SetDefinition(replacement);
            var applied = table.GetDefinition();
            return (object)new
            {
                map = map.Name,
                table = table.Name,
                cim_type = applied.GetType().Name,
                cim_json = applied.ToJson(),
                source_connection_preserved = BridgeSupport.Bool(payload, "preserve_source_connection", true),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> GetReportAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var reportName = Required(payload, "report_name");
        return await QueuedTask.Run<object>(() =>
        {
            var report = ResolveReport(reportName);
            var definition = report.GetDefinition();
            return (object)new
            {
                report = report.Name,
                cim_type = definition.GetType().Name,
                cim_json = definition.ToJson(),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> SetReportAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        RequireConfirmation(payload);
        var reportName = Required(payload, "report_name");
        var cimJson = Required(payload, "cim_json");
        return await QueuedTask.Run<object>(() =>
        {
            var report = ResolveReport(reportName);
            var current = report.GetDefinition();
            var replacement = ParseSameType(current, cimJson);
            var preserveIdentity = BridgeSupport.Bool(payload, "preserve_identity", true);
            var preserveSections = BridgeSupport.Bool(payload, "preserve_sections", true);
            var preserveMapSeries = BridgeSupport.Bool(payload, "preserve_map_series", true);
            if (!preserveSections || !preserveMapSeries)
            {
                throw new NotSupportedException(
                    "ArcGIS Pro 3.6.3 hangs when replacing a complete report definition. "
                    + "Keep preserve_sections=true and preserve_map_series=true; use "
                    + "set_report_element_cim and typed report actions for section changes.");
            }
            var appliedProperties = new List<string>();
            if (!preserveIdentity
                && !string.IsNullOrWhiteSpace(replacement.Name)
                && !string.Equals(replacement.Name, report.Name, StringComparison.Ordinal))
            {
                report.SetName(replacement.Name);
                appliedProperties.Add("name");
            }
            if (replacement.Page is not null)
            {
                report.SetPage(replacement.Page);
                appliedProperties.Add("page");
            }
            var applied = report.GetDefinition();
            return (object)new
            {
                report = report.Name,
                cim_type = applied.GetType().Name,
                cim_json = applied.ToJson(),
                identity_preserved = preserveIdentity,
                sections_preserved = true,
                map_series_preserved = true,
                full_definition_replaced = false,
                applied_properties = appliedProperties,
                sdk_limitation = "Complete Report.SetDefinition is disabled because ArcGIS Pro 3.6.3 hangs on a same-definition round trip.",
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> GetReportElementAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var reportName = Required(payload, "report_name");
        var elementName = Required(payload, "element_name");
        return await QueuedTask.Run<object>(() =>
        {
            var report = ResolveReport(reportName);
            var element = report.FindElement(elementName)
                ?? throw new InvalidOperationException($"Report element not found: {elementName}");
            return DescribeElement("report", report.Name, element);
        });
    }

    internal static async Task<object> SetReportElementAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        RequireConfirmation(payload);
        var reportName = Required(payload, "report_name");
        var elementName = Required(payload, "element_name");
        var cimJson = Required(payload, "cim_json");
        return await QueuedTask.Run<object>(() =>
        {
            var report = ResolveReport(reportName);
            var element = report.FindElement(elementName)
                ?? throw new InvalidOperationException($"Report element not found: {elementName}");
            var current = element.GetDefinition();
            var replacement = ParseSameType(current, cimJson);
            PreserveElementLinks(current, replacement, payload);
            element.SetDefinition(replacement);
            return DescribeElement("report", report.Name, element, replaced: true);
        });
    }

    internal static async Task<object> GetPresentationAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var name = Required(payload, "presentation_name");
        return await QueuedTask.Run<object>(() =>
        {
            var presentation = ResolvePresentation(name);
            var definition = presentation.GetDefinition();
            return (object)new
            {
                presentation = presentation.Name,
                cim_type = definition.GetType().Name,
                cim_json = definition.ToJson(),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> SetPresentationAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        RequireConfirmation(payload);
        var name = Required(payload, "presentation_name");
        var cimJson = Required(payload, "cim_json");
        return await QueuedTask.Run<object>(() =>
        {
            var presentation = ResolvePresentation(name);
            var current = presentation.GetDefinition();
            var replacement = ParseSameType(current, cimJson);
            if (BridgeSupport.Bool(payload, "preserve_identity", true))
            {
                replacement.Name = current.Name;
                replacement.URI = current.URI;
                replacement.SourceURI = current.SourceURI;
                replacement.SourcePortalUrl = current.SourcePortalUrl;
            }
            if (BridgeSupport.Bool(payload, "preserve_pages", true))
            {
                replacement.Pages = current.Pages;
            }
            presentation.SetDefinition(replacement);
            var applied = presentation.GetDefinition();
            return (object)new
            {
                presentation = presentation.Name,
                cim_type = applied.GetType().Name,
                cim_json = applied.ToJson(),
                identity_preserved = BridgeSupport.Bool(payload, "preserve_identity", true),
                pages_preserved = BridgeSupport.Bool(payload, "preserve_pages", true),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> GetPresentationPageAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var name = Required(payload, "presentation_name");
        var pageIndex = BridgeSupport.Int(payload, "page_index", -1);
        return await QueuedTask.Run<object>(() =>
        {
            var presentation = ResolvePresentation(name);
            var page = ResolvePresentationPage(presentation, pageIndex);
            var definition = page.GetDefinition();
            return (object)new
            {
                presentation = presentation.Name,
                page_index = pageIndex,
                page_type = page.PageType.ToString(),
                cim_type = definition.GetType().Name,
                cim_json = definition.ToJson(),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> SetPresentationPageAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        RequireConfirmation(payload);
        var name = Required(payload, "presentation_name");
        var pageIndex = BridgeSupport.Int(payload, "page_index", -1);
        var cimJson = Required(payload, "cim_json");
        return await QueuedTask.Run<object>(() =>
        {
            var presentation = ResolvePresentation(name);
            var page = ResolvePresentationPage(presentation, pageIndex);
            var current = page.GetDefinition();
            var replacement = ParseSameType(current, cimJson);
            if (BridgeSupport.Bool(payload, "preserve_structural_links", true))
            {
                PreserveProperties(
                    current,
                    replacement,
                    "ElementStorageURI",
                    "ThumbnailURI",
                    "MapView",
                    "ImageURI",
                    "ImageURL",
                    "VideoURI",
                    "VideoURL");
            }
            page.SetDefinition(replacement);
            var applied = page.GetDefinition();
            return (object)new
            {
                presentation = presentation.Name,
                page_index = pageIndex,
                page_type = page.PageType.ToString(),
                cim_type = applied.GetType().Name,
                cim_json = applied.ToJson(),
                structural_links_preserved = BridgeSupport.Bool(payload, "preserve_structural_links", true),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> GetPresentationPageElementAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var name = Required(payload, "presentation_name");
        var pageIndex = BridgeSupport.Int(payload, "page_index", -1);
        var elementName = Required(payload, "element_name");
        return await QueuedTask.Run<object>(() =>
        {
            var presentation = ResolvePresentation(name);
            var page = ResolvePresentationPage(presentation, pageIndex);
            var element = page.FindElement(elementName)
                ?? throw new InvalidOperationException($"Presentation page element not found: {elementName}");
            return DescribePresentationElement(presentation, page, pageIndex, element);
        });
    }

    internal static async Task<object> SetPresentationPageElementAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        RequireConfirmation(payload);
        var name = Required(payload, "presentation_name");
        var pageIndex = BridgeSupport.Int(payload, "page_index", -1);
        var elementName = Required(payload, "element_name");
        var cimJson = Required(payload, "cim_json");
        return await QueuedTask.Run<object>(() =>
        {
            var presentation = ResolvePresentation(name);
            var page = ResolvePresentationPage(presentation, pageIndex);
            var element = page.FindElement(elementName)
                ?? throw new InvalidOperationException($"Presentation page element not found: {elementName}");
            var current = element.GetDefinition();
            var replacement = ParseSameType(current, cimJson);
            PreserveElementLinks(current, replacement, payload);
            element.SetDefinition(replacement);
            return DescribePresentationElement(presentation, page, pageIndex, element, replaced: true);
        });
    }

    private static object DescribeElement(
        string ownerType,
        string ownerName,
        Element element,
        bool replaced = false,
        bool created = false)
    {
        var definition = element.GetDefinition();
        return new
        {
            owner_type = ownerType,
            owner_name = ownerName,
            element = element.Name,
            element_type = element.GetType().Name,
            cim_type = definition.GetType().Name,
            cim_json = definition.ToJson(),
            definition_replaced = replaced,
            created,
            source_data_modified = false,
        };
    }

    private static object DescribePresentationElement(
        Presentation presentation,
        PresentationPage page,
        int pageIndex,
        Element element,
        bool replaced = false)
    {
        var definition = element.GetDefinition();
        return new
        {
            presentation = presentation.Name,
            page_index = pageIndex,
            page_type = page.PageType.ToString(),
            element = element.Name,
            element_type = element.GetType().Name,
            cim_type = definition.GetType().Name,
            cim_json = definition.ToJson(),
            definition_replaced = replaced,
            source_data_modified = false,
        };
    }

    private static Element ResolveElement(Layout layout, string elementName)
    {
        return layout.FindElement(elementName)
            ?? throw new InvalidOperationException($"Layout element not found: {elementName}");
    }

    private static StandaloneTable ResolveStandaloneTable(Map map, string tableName)
    {
        return map.GetStandaloneTablesAsFlattenedList()
            .FirstOrDefault(table => string.Equals(
                table.Name, tableName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Standalone table not found: {tableName}");
    }

    private static Report ResolveReport(string reportName)
    {
        return Project.Current.GetItems<ReportProjectItem>()
            .FirstOrDefault(item => string.Equals(
                item.Name, reportName, StringComparison.OrdinalIgnoreCase))
            ?.GetReport()
            ?? throw new InvalidOperationException($"Report not found: {reportName}");
    }

    private static Presentation ResolvePresentation(string name)
    {
        return Project.Current.GetItems<PresentationProjectItem>()
            .FirstOrDefault(item => string.Equals(
                item.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.GetPresentation()
            ?? throw new InvalidOperationException($"Presentation not found: {name}");
    }

    private static PresentationPage ResolvePresentationPage(Presentation presentation, int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= presentation.PageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageIndex),
                $"page_index must be between 0 and {Math.Max(0, presentation.PageCount - 1)}");
        }
        return presentation.GetPage(pageIndex);
    }

    internal static void PreserveElementLinks(
        CIMElement current,
        CIMElement replacement,
        JsonElement payload)
    {
        if (BridgeSupport.Bool(payload, "preserve_name", true))
        {
            replacement.Name = current.Name;
        }
        if (BridgeSupport.Bool(payload, "preserve_structural_links", true))
        {
            PreserveProperties(
                current,
                replacement,
                "URI",
                "SourceURI",
                "SourcePortalUrl",
                "DataConnection",
                "SelectionSetURI",
                "MapFrame",
                "MapMemberURI",
                "ChartName",
                "ElementStorageURI",
                "PresentationURI");
        }
    }

    private static void PreserveProperties(object current, object replacement, params string[] names)
    {
        var type = current.GetType();
        if (replacement.GetType() != type)
        {
            throw new ArgumentException("CIM definition type cannot change");
        }
        foreach (var name in names)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property is null || !property.CanRead || !property.CanWrite)
            {
                continue;
            }
            property.SetValue(replacement, property.GetValue(current));
        }
    }

    internal static T ParseSameType<T>(T current, string cimJson) where T : class
    {
        var parsed = InvokeFromJson(current.GetType(), cimJson) as T
            ?? throw new ArgumentException($"Could not parse CIM as {current.GetType().Name}");
        if (parsed.GetType() != current.GetType())
        {
            throw new ArgumentException(
                $"CIM type cannot change from {current.GetType().Name} to {parsed.GetType().Name}");
        }
        return parsed;
    }

    internal static T ParseNamedType<T>(string cimType, string cimJson) where T : class
    {
        var fullName = cimType.Contains('.') ? cimType : $"ArcGIS.Core.CIM.{cimType}";
        var type = typeof(CIMElement).Assembly.GetType(fullName, throwOnError: false, ignoreCase: true)
            ?? throw new ArgumentException($"Unknown CIM type: {cimType}");
        if (!typeof(T).IsAssignableFrom(type) || type.IsAbstract)
        {
            throw new ArgumentException($"CIM type is not a concrete {typeof(T).Name}: {cimType}");
        }
        return InvokeFromJson(type, cimJson) as T
            ?? throw new ArgumentException($"Could not parse CIM as {type.Name}");
    }

    private static object InvokeFromJson(Type type, string cimJson)
    {
        var method = type.GetMethod(
            "FromJson",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(string), typeof(JsonDeserializationSettings) },
            null)
            ?? throw new InvalidOperationException($"CIM type does not expose FromJson: {type.Name}");
        try
        {
            return method.Invoke(
                null,
                new object[] { cimJson, new JsonDeserializationSettings() })
                ?? throw new ArgumentException($"Could not parse CIM as {type.Name}");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new ArgumentException(
                $"Invalid {type.Name} CIM JSON: {exception.InnerException.Message}",
                exception.InnerException);
        }
    }

    private static string Required(JsonElement payload, string name)
    {
        return BridgeSupport.String(payload, name)
            ?? throw new ArgumentException($"{name} is required");
    }

    private static void RequireConfirmation(JsonElement payload)
    {
        if (!BridgeSupport.Bool(payload, "confirm_replace_definition"))
        {
            throw new ArgumentException("confirm_replace_definition=true is required");
        }
    }
}
