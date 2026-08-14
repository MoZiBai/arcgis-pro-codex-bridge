using System.IO;
using System.Text.Json;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using ArcGIS.Desktop.Presentations;

namespace ArcGISCodexBridgeAddin;

internal static class PresentationActions
{
    internal static async Task<object> ListAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        return await QueuedTask.Run<object>(() =>
        {
            var presentations = Project.Current.GetItems<PresentationProjectItem>()
                .Select(item =>
                {
                    var presentation = item.GetPresentation();
                    var ratio = presentation.GetAspectRatio();
                    return new
                    {
                        name = presentation.Name,
                        uri = presentation.URI,
                        page_count = presentation.PageCount,
                        aspect_ratio = new { width = ratio.Item1, height = ratio.Item2 },
                        pages = presentation.GetAllPages().Select((page, index) => PageInfo(page, index)).ToArray(),
                    };
                }).ToArray();
            return (object)new { presentations };
        });
    }

    internal static async Task<object> CreateAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var name = BridgeSupport.String(payload, "presentation_name")
            ?? throw new ArgumentException("presentation_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            EnsureUnique(name);
            var presentation = PresentationFactory.Instance.CreatePresentation(name);
            var width = Math.Clamp(BridgeSupport.Int(payload, "aspect_width", 16), 1, 100);
            var height = Math.Clamp(BridgeSupport.Int(payload, "aspect_height", 9), 1, 100);
            presentation.SetAspectRatio(width, height);
            return (object)new
            {
                presentation = presentation.Name,
                uri = presentation.URI,
                aspect_ratio = new { width, height },
            };
        });
    }

    internal static async Task<object> DuplicateAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var name = BridgeSupport.String(payload, "presentation_name")
            ?? throw new ArgumentException("presentation_name is required");
        var newName = BridgeSupport.String(payload, "new_name")
            ?? throw new ArgumentException("new_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            EnsureUnique(newName);
            var source = Resolve(name);
            var copy = PresentationFactory.Instance.CopyPresentation(source);
            copy.SetName(newName);
            return (object)new
            {
                source_presentation = source.Name,
                presentation = copy.Name,
                page_count = copy.PageCount,
            };
        });
    }

    internal static async Task<object> RenameAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var name = BridgeSupport.String(payload, "presentation_name")
            ?? throw new ArgumentException("presentation_name is required");
        var newName = BridgeSupport.String(payload, "new_name")
            ?? throw new ArgumentException("new_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            EnsureUnique(newName);
            var presentation = Resolve(name);
            presentation.SetName(newName);
            return (object)new { old_name = name, presentation = presentation.Name };
        });
    }

    internal static async Task<object> RemoveAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var name = BridgeSupport.String(payload, "presentation_name")
            ?? throw new ArgumentException("presentation_name is required");
        if (!BridgeSupport.Bool(payload, "confirm_remove", false))
        {
            throw new ArgumentException("confirm_remove=true is required to remove a presentation project item");
        }
        return await QueuedTask.Run<object>(() =>
        {
            var item = ResolveItem(name);
            var removed = Project.Current.RemoveItem(item);
            return (object)new { presentation = name, removed_from_project = removed };
        });
    }

    internal static async Task<object> OpenAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var name = BridgeSupport.String(payload, "presentation_name")
            ?? throw new ArgumentException("presentation_name is required");
        var presentation = await QueuedTask.Run(() => Resolve(name));
        await await FrameworkApplication.Current.Dispatcher.InvokeAsync(
            () => FrameworkApplication.Panes.CreatePresentationPaneAsync(presentation));
        return new { presentation = presentation.Name, opened = true };
    }

    internal static async Task<object> ConfigureAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var name = BridgeSupport.String(payload, "presentation_name")
            ?? throw new ArgumentException("presentation_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            var presentation = Resolve(name);
            if (payload.TryGetProperty("aspect_width", out _)
                || payload.TryGetProperty("aspect_height", out _))
            {
                var current = presentation.GetAspectRatio();
                presentation.SetAspectRatio(
                    Math.Clamp(BridgeSupport.Int(payload, "aspect_width", current.Item1), 1, 100),
                    Math.Clamp(BridgeSupport.Int(payload, "aspect_height", current.Item2), 1, 100));
            }
            var ratio = presentation.GetAspectRatio();
            return (object)new
            {
                presentation = presentation.Name,
                aspect_ratio = new { width = ratio.Item1, height = ratio.Item2 },
            };
        });
    }

    internal static async Task<object> AddPageAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var name = BridgeSupport.String(payload, "presentation_name")
            ?? throw new ArgumentException("presentation_name is required");
        var pageType = BridgeSupport.String(payload, "page_type", "blank")!.ToLowerInvariant();
        return await QueuedTask.Run<object>(() =>
        {
            var presentation = Resolve(name);
            var index = Math.Clamp(
                BridgeSupport.Int(payload, "insert_index", presentation.PageCount),
                0,
                presentation.PageCount);
            PresentationPage page = pageType switch
            {
                "blank" => presentation.AddBlankPage(ParseBlankTemplate(
                    BridgeSupport.String(payload, "blank_template", "Blank")), index),
                "map" => presentation.AddMapPage(
                    BridgeSupport.ResolveMap(BridgeSupport.String(payload, "map_name")), index),
                "image" => presentation.AddImagePage(
                    BridgeSupport.String(payload, "media_path")
                        ?? throw new ArgumentException("media_path is required for an image page"),
                    index),
                "video" => presentation.AddVideoPage(
                    BridgeSupport.String(payload, "media_path")
                        ?? throw new ArgumentException("media_path is required for a video page"),
                    index),
                _ => throw new ArgumentException($"Unsupported presentation page type: {pageType}"),
            };
            ApplyPageProperties(page, payload);
            if (page is MapPresentationPage mapPage)
            {
                var extentLayer = BridgeSupport.String(payload, "extent_layer_name");
                if (!string.IsNullOrWhiteSpace(extentLayer))
                {
                    var map = BridgeSupport.ResolveMap(BridgeSupport.String(payload, "map_name"));
                    mapPage.SetCamera(BridgeSupport.ResolveLayer(map, extentLayer));
                }
            }
            return (object)new
            {
                presentation = presentation.Name,
                inserted_index = index,
                page = PageInfo(page, index),
                page_count = presentation.PageCount,
            };
        });
    }

    internal static async Task<object> ConfigurePageAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var name = BridgeSupport.String(payload, "presentation_name")
            ?? throw new ArgumentException("presentation_name is required");
        var index = BridgeSupport.Int(payload, "page_index", -1);
        return await QueuedTask.Run<object>(() =>
        {
            var presentation = Resolve(name);
            if (index < 0 || index >= presentation.PageCount)
            {
                throw new ArgumentOutOfRangeException("page_index");
            }
            var page = presentation.GetPage(index);
            ApplyPageProperties(page, payload);
            if (page is MapPresentationPage mapPage)
            {
                var requestedMap = BridgeSupport.String(payload, "map_name");
                if (!string.IsNullOrWhiteSpace(requestedMap))
                {
                    mapPage.SetMapSource(BridgeSupport.ResolveMap(requestedMap));
                }
                var extentLayer = BridgeSupport.String(payload, "extent_layer_name");
                if (!string.IsNullOrWhiteSpace(extentLayer))
                {
                    var map = BridgeSupport.ResolveMap(requestedMap);
                    mapPage.SetCamera(BridgeSupport.ResolveLayer(map, extentLayer));
                }
            }
            if (page is ImagePresentationPage imagePage
                && !string.IsNullOrWhiteSpace(BridgeSupport.String(payload, "media_path")))
            {
                imagePage.SetImageSource(BridgeSupport.String(payload, "media_path")!);
            }
            if (page is VideoPresentationPage videoPage)
            {
                if (!string.IsNullOrWhiteSpace(BridgeSupport.String(payload, "media_path")))
                {
                    videoPage.SetVideoSource(BridgeSupport.String(payload, "media_path")!);
                }
                if (payload.TryGetProperty("start_time", out _))
                {
                    videoPage.SetStartTime(Math.Max(0, BridgeSupport.Double(payload, "start_time")));
                }
                if (payload.TryGetProperty("end_time", out _))
                {
                    videoPage.SetEndTime(Math.Max(0, BridgeSupport.Double(payload, "end_time")));
                }
            }
            return (object)new
            {
                presentation = presentation.Name,
                page = PageInfo(page, index),
            };
        });
    }

    internal static async Task<object> RemovePagesAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var name = BridgeSupport.String(payload, "presentation_name")
            ?? throw new ArgumentException("presentation_name is required");
        if (!BridgeSupport.Bool(payload, "confirm_remove", false))
        {
            throw new ArgumentException("confirm_remove=true is required to delete presentation pages");
        }
        var indices = payload.TryGetProperty("page_indices", out var values)
            && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.Number)
                .Select(value => value.GetInt32())
                .Distinct()
                .OrderByDescending(value => value)
                .ToArray()
            : Array.Empty<int>();
        if (indices.Length == 0)
        {
            throw new ArgumentException("page_indices must contain at least one page index");
        }
        return await QueuedTask.Run<object>(() =>
        {
            var presentation = Resolve(name);
            if (indices.Any(index => index < 0 || index >= presentation.PageCount))
            {
                throw new ArgumentOutOfRangeException("page_indices");
            }
            presentation.DeletePages(indices);
            return (object)new
            {
                presentation = presentation.Name,
                removed_page_indices = indices,
                page_count = presentation.PageCount,
            };
        });
    }

    internal static async Task<object> ExportAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var name = BridgeSupport.String(payload, "presentation_name")
            ?? throw new ArgumentException("presentation_name is required");
        var output = BridgeSupport.String(payload, "output_file")
            ?? throw new ArgumentException("output_file is required");
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? string.Empty);
        return await QueuedTask.Run<object>(() =>
        {
            var presentation = Resolve(name);
            var format = CreateFormat(output, Math.Clamp(BridgeSupport.Int(payload, "dpi", 300), 72, 1200));
            var options = new PresentationExportOptions();
            var customPages = BridgeSupport.String(payload, "custom_pages");
            if (!string.IsNullOrWhiteSpace(customPages))
            {
                options.PageRangeOption = ExportPageOptions.ExportByPageRange;
                options.CustomPages = customPages;
            }
            presentation.Export(format, options);
            return (object)new
            {
                presentation = presentation.Name,
                output,
                page_range = options.PageRangeOption.ToString(),
                custom_pages = options.CustomPages,
            };
        });
    }

    internal static async Task<object> SaveFileAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var name = BridgeSupport.String(payload, "presentation_name")
            ?? throw new ArgumentException("presentation_name is required");
        var output = BridgeSupport.String(payload, "output_file")
            ?? throw new ArgumentException("output_file is required");
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? string.Empty);
        return await QueuedTask.Run<object>(() =>
        {
            var presentation = Resolve(name);
            presentation.SaveAsFile(output, BridgeSupport.Bool(payload, "overwrite", false));
            return (object)new { presentation = presentation.Name, output };
        });
    }

    private static void ApplyPageProperties(PresentationPage page, JsonElement payload)
    {
        if (payload.TryGetProperty("visible", out _))
        {
            page.SetIsVisible(BridgeSupport.Bool(payload, "visible", true));
        }
        if (payload.TryGetProperty("locked", out _))
        {
            page.SetIsLocked(BridgeSupport.Bool(payload, "locked", false));
        }
        if (payload.TryGetProperty("automatic_advancement", out _))
        {
            page.SetAutomaticAdvancement(BridgeSupport.Bool(payload, "automatic_advancement", false));
        }
        if (payload.TryGetProperty("hold_time", out _))
        {
            page.SetHoldTime(Math.Max(0, BridgeSupport.Double(payload, "hold_time")));
        }
        if (payload.TryGetProperty("background_color", out _))
        {
            page.SetBackgroundColor(BridgeSupport.Color(payload, "background_color"));
        }
    }

    private static object PageInfo(PresentationPage page, int index)
    {
        var elements = page.GetFlattenedElements();
        return new
        {
            index,
            page_type = page.PageType.ToString(),
            visible = page.IsVisible,
            locked = page.IsLocked,
            automatic_advancement = page.IsAutomaticAdvancement,
            hold_time = page.HoldTime,
            element_count = elements.Count,
            elements = elements.Select(element => new
            {
                name = element.Name,
                type = element.GetType().Name,
                visible = element.IsVisible,
            }).ToArray(),
        };
    }

    private static BlankPageTemplateType ParseBlankTemplate(string? value)
    {
        return Enum.TryParse<BlankPageTemplateType>(value, true, out var parsed)
            ? parsed
            : BlankPageTemplateType.Blank;
    }

    private static Presentation Resolve(string name) => ResolveItem(name).GetPresentation();

    private static PresentationProjectItem ResolveItem(string name)
    {
        return Project.Current.GetItems<PresentationProjectItem>()
            .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Presentation not found: {name}");
    }

    private static void EnsureUnique(string name)
    {
        if (Project.Current.GetItems<PresentationProjectItem>()
            .Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Presentation already exists: {name}");
        }
    }

    private static ExportFormat CreateFormat(string output, int dpi)
    {
        return Path.GetExtension(output).ToLowerInvariant() switch
        {
            ".pdf" => new PDFFormat { OutputFileName = output, Resolution = dpi },
            ".png" => new PNGFormat { OutputFileName = output, Resolution = dpi },
            ".jpg" or ".jpeg" => new JPEGFormat { OutputFileName = output, Resolution = dpi },
            ".tif" or ".tiff" => new TIFFFormat { OutputFileName = output, Resolution = dpi },
            ".svg" => new SVGFormat { OutputFileName = output, Resolution = dpi },
            ".svgz" => new SVGFormat
            {
                OutputFileName = output,
                Resolution = dpi,
                DoCompressToSVGZ = true,
            },
            _ => throw new ArgumentException($"Unsupported presentation export format: {output}"),
        };
    }
}
