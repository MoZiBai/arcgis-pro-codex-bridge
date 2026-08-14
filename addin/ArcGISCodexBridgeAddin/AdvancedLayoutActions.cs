using System.Text.Json;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;

namespace ArcGISCodexBridgeAddin;

internal static class AdvancedLayoutActions
{
    internal static async Task<object> ArrangeElementsAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var names = BridgeSupport.Strings(payload, "element_names");
        if (names.Length < 2)
        {
            throw new ArgumentException("element_names must contain at least two elements");
        }
        var operation = BridgeSupport.String(payload, "operation")?.ToLowerInvariant()
            ?? throw new ArgumentException("operation is required");
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var elements = names.Select(name => layout.FindElement(name)
                ?? throw new InvalidOperationException($"Layout element not found: {name}"))
                .ToArray();
            var referenceName = BridgeSupport.String(payload, "reference_element_name");
            var reference = !string.IsNullOrWhiteSpace(referenceName)
                ? elements.FirstOrDefault(item => string.Equals(
                    item.Name, referenceName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException("reference_element_name must be included in element_names")
                : elements[0];
            switch (operation)
            {
                case "align_left":
                    Align(elements, reference.GetGeometry().Extent.XMin, horizontal: true, edge: "min");
                    break;
                case "align_horizontal_center":
                    Align(elements, reference.GetGeometry().Extent.XMin + reference.GetGeometry().Extent.Width / 2, horizontal: true, edge: "center");
                    break;
                case "align_right":
                    Align(elements, reference.GetGeometry().Extent.XMax, horizontal: true, edge: "max");
                    break;
                case "align_bottom":
                    Align(elements, reference.GetGeometry().Extent.YMin, horizontal: false, edge: "min");
                    break;
                case "align_vertical_center":
                    Align(elements, reference.GetGeometry().Extent.YMin + reference.GetGeometry().Extent.Height / 2, horizontal: false, edge: "center");
                    break;
                case "align_top":
                    Align(elements, reference.GetGeometry().Extent.YMax, horizontal: false, edge: "max");
                    break;
                case "distribute_horizontal":
                    Distribute(elements, horizontal: true);
                    break;
                case "distribute_vertical":
                    Distribute(elements, horizontal: false);
                    break;
                case "same_width":
                    foreach (var element in elements) element.SetWidth(reference.GetWidth());
                    break;
                case "same_height":
                    foreach (var element in elements) element.SetHeight(reference.GetHeight());
                    break;
                case "same_size":
                    foreach (var element in elements)
                    {
                        element.SetWidth(reference.GetWidth());
                        element.SetHeight(reference.GetHeight());
                    }
                    break;
                default:
                    throw new ArgumentException($"Unsupported layout arrangement operation: {operation}");
            }
            return (object)new
            {
                layout = layout.Name,
                operation,
                reference_element = reference.Name,
                elements = elements.Select(element => new
                {
                    name = element.Name,
                    x = element.GetX(),
                    y = element.GetY(),
                    width = element.GetWidth(),
                    height = element.GetHeight(),
                }).ToArray(),
                source_data_modified = false,
            };
        });
    }

    private static void Align(Element[] elements, double target, bool horizontal, string edge)
    {
        foreach (var element in elements)
        {
            var extent = element.GetGeometry().Extent;
            var current = edge switch
            {
                "min" => horizontal ? extent.XMin : extent.YMin,
                "max" => horizontal ? extent.XMax : extent.YMax,
                _ => horizontal ? extent.XMin + extent.Width / 2 : extent.YMin + extent.Height / 2,
            };
            if (horizontal)
                element.SetX(element.GetX() + target - current);
            else
                element.SetY(element.GetY() + target - current);
        }
    }

    private static void Distribute(Element[] elements, bool horizontal)
    {
        if (elements.Length < 3)
        {
            throw new ArgumentException("Distribution requires at least three elements");
        }
        var ordered = elements.OrderBy(element => horizontal
            ? element.GetGeometry().Extent.XMin
            : element.GetGeometry().Extent.YMin).ToArray();
        var first = ordered[0].GetGeometry().Extent;
        var last = ordered[^1].GetGeometry().Extent;
        var start = horizontal ? first.XMin : first.YMin;
        var end = horizontal ? last.XMax : last.YMax;
        var occupied = ordered.Sum(element => horizontal
            ? element.GetGeometry().Extent.Width
            : element.GetGeometry().Extent.Height);
        var gap = (end - start - occupied) / (ordered.Length - 1);
        var cursor = start;
        foreach (var element in ordered)
        {
            var extent = element.GetGeometry().Extent;
            var minimum = horizontal ? extent.XMin : extent.YMin;
            if (horizontal)
                element.SetX(element.GetX() + cursor - minimum);
            else
                element.SetY(element.GetY() + cursor - minimum);
            cursor += (horizontal ? extent.Width : extent.Height) + gap;
        }
    }
}
