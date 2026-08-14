using System.Text.Json;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class ExploratoryAnalysisActions
{
    internal static async Task<object> ManageAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var operation = (BridgeSupport.String(payload, "operation") ?? "inspect")
            .Trim().ToLowerInvariant();
        return await QueuedTask.Run<object>(async () =>
        {
            var view = ResolveSceneView(BridgeSupport.String(payload, "map_name"));
            var collection = view.GetExploratoryAnalysisCollection().ToList();
            if (operation == "inspect")
                return DescribeCollection(view, collection, false);

            if (operation == "add")
            {
                var analysis = CreateAnalysis(payload, view);
                await view.AddExploratoryAnalysisAsync(analysis);
                var result = DescribeCollection(
                    view, view.GetExploratoryAnalysisCollection().ToList(), true);
                return new
                {
                    operation,
                    added = DescribeAnalysis(analysis),
                    state = result,
                    project_modified = true,
                    source_data_modified = false,
                    project_saved = false,
                };
            }

            if (operation == "remove")
            {
                if (!BridgeSupport.Bool(payload, "confirm_remove", false))
                    throw new ArgumentException("confirm_remove=true is required to remove exploratory analysis");
                var id = RequiredInt(payload, "analysis_id");
                var analysis = collection.FirstOrDefault(item => item.ID == id)
                    ?? throw new InvalidOperationException($"Exploratory analysis not found: {id}");
                await view.RemoveExploratoryAnalysisAsync(analysis);
                return new
                {
                    operation,
                    removed_id = id,
                    removed_type = analysis.GetType().Name,
                    state = DescribeCollection(
                        view, view.GetExploratoryAnalysisCollection().ToList(), true),
                    project_modified = true,
                    source_data_modified = false,
                    project_saved = false,
                };
            }

            if (operation == "clear")
            {
                if (!BridgeSupport.Bool(payload, "confirm_clear", false))
                    throw new ArgumentException("confirm_clear=true is required to clear exploratory analysis");
                var removed = collection.Select(item => new { id = item.ID, type = item.GetType().Name }).ToArray();
                foreach (var item in collection)
                    await view.RemoveExploratoryAnalysisAsync(item);
                return new
                {
                    operation,
                    removed,
                    state = DescribeCollection(
                        view, view.GetExploratoryAnalysisCollection().ToList(), true),
                    project_modified = removed.Length > 0,
                    source_data_modified = false,
                    project_saved = false,
                };
            }

            throw new ArgumentException("operation must be inspect, add, remove, or clear");
        });
    }

    private static ExploratoryAnalysis CreateAnalysis(JsonElement payload, MapView view)
    {
        var type = (BridgeSupport.String(payload, "analysis_type") ?? string.Empty)
            .Trim().ToLowerInvariant();
        var observer = ParseCamera(payload, "observer", view);
        var minimum = Math.Max(0, BridgeSupport.Double(payload, "minimum_distance", 0));
        var maximum = BridgeSupport.Double(payload, "maximum_distance", 1000);
        if (!double.IsFinite(maximum) || maximum <= minimum)
            throw new ArgumentException("maximum_distance must be finite and greater than minimum_distance");
        return type switch
        {
            "viewshed" => new Viewshed(
                observer,
                ClampAngle(BridgeSupport.Double(payload, "vertical_angle", 60), 0.1, 180),
                ClampAngle(BridgeSupport.Double(payload, "horizontal_angle", 90), 0.1, 360),
                minimum,
                maximum),
            "view_dome" or "viewdome" => new ViewDome(observer, minimum, maximum),
            "line_of_sight" or "lineofsight" => new LineOfSight(
                observer,
                ParseTargets(payload, view),
                minimum,
                maximum),
            _ => throw new ArgumentException(
                "analysis_type must be viewshed, view_dome, or line_of_sight"),
        };
    }

    private static List<Camera> ParseTargets(JsonElement payload, MapView view)
    {
        if (!payload.TryGetProperty("targets", out var targets)
            || targets.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("targets array is required for line_of_sight");
        }
        var result = targets.EnumerateArray()
            .Take(1000)
            .Select((target, index) => ParseCamera(target, $"targets[{index}]", view))
            .ToList();
        if (result.Count == 0)
            throw new ArgumentException("At least one target is required for line_of_sight");
        return result;
    }

    private static Camera ParseCamera(JsonElement payload, string property, MapView view)
    {
        JsonElement value;
        if (property.Contains('['))
            value = payload;
        else if (!payload.TryGetProperty(property, out value) || value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"{property} object is required");
        var x = RequiredFinite(value, "x", property);
        var y = RequiredFinite(value, "y", property);
        var z = RequiredFinite(value, "z", property);
        var pitch = BridgeSupport.Double(value, "pitch", -45);
        var heading = BridgeSupport.Double(value, "heading", 0);
        var spatialReference = value.TryGetProperty("wkid", out var wkid) && wkid.TryGetInt32(out var code)
            ? SpatialReferenceBuilder.CreateSpatialReference(code)
            : view.Map.SpatialReference;
        var viewpointText = BridgeSupport.String(value, "viewpoint", "LookFrom");
        if (!Enum.TryParse<CameraViewpoint>(viewpointText, true, out var viewpoint))
            throw new ArgumentException($"{property}.viewpoint must be LookFrom or LookAt");
        var camera = new Camera(x, y, z, pitch, heading, spatialReference, viewpoint)
        {
            Roll = BridgeSupport.Double(value, "roll", 0),
        };
        return camera;
    }

    private static object DescribeCollection(
        MapView view,
        IReadOnlyCollection<ExploratoryAnalysis> collection,
        bool modified)
    {
        return new
        {
            map = view.Map.Name,
            is_scene = view.Map.IsScene,
            analysis_count = collection.Count,
            analyses = collection.Select(DescribeAnalysis).ToArray(),
            project_modified = modified,
            source_data_modified = false,
            project_saved = false,
        };
    }

    private static object DescribeAnalysis(ExploratoryAnalysis analysis)
    {
        return analysis switch
        {
            Viewshed item => new
            {
                id = item.ID,
                type = "Viewshed",
                observer = DescribeCamera(item.GetObserver()),
                vertical_angle = Finite(item.VerticalAngle),
                horizontal_angle = Finite(item.HorizontalAngle),
                minimum_distance = Finite(item.MinimumDistance),
                maximum_distance = Finite(item.MaximumDistance),
            },
            ViewDome item => new
            {
                id = item.ID,
                type = "ViewDome",
                observer = DescribeCamera(item.GetObserver()),
                minimum_distance = Finite(item.MinimumDistance),
                maximum_distance = Finite(item.MaximumDistance),
            },
            LineOfSight item => new
            {
                id = item.ID,
                type = "LineOfSight",
                observer = DescribeCamera(item.GetObserver()),
                targets = item.Targets.Take(1000).Select(DescribeCamera).ToArray(),
                minimum_distance = Finite(item.MinimumDistance),
                maximum_distance = Finite(item.MaximumDistance),
            },
            _ => new
            {
                id = analysis.ID,
                type = analysis.GetType().Name,
            },
        };
    }

    private static object DescribeCamera(Camera camera)
    {
        return new
        {
            x = Finite(camera.X),
            y = Finite(camera.Y),
            z = Finite(camera.Z),
            pitch = Finite(camera.Pitch),
            heading = Finite(camera.Heading),
            roll = Finite(camera.Roll),
            viewpoint = camera.Viewpoint.ToString(),
            wkid = camera.SpatialReference?.Wkid,
        };
    }

    private static MapView ResolveSceneView(string? requestedMap)
    {
        var view = MapView.Active
            ?? throw new InvalidOperationException("There is no active map view");
        if (!string.IsNullOrWhiteSpace(requestedMap)
            && !string.Equals(view.Map.Name, requestedMap, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The requested map must be the active map view");
        }
        if (!view.Map.IsScene)
            throw new InvalidOperationException("Exploratory analysis requires an active scene view");
        return view;
    }

    private static int RequiredInt(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result))
            throw new ArgumentException($"{name} is required and must be an integer");
        return result;
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

    private static double ClampAngle(double value, double minimum, double maximum)
    {
        if (!double.IsFinite(value))
            throw new ArgumentException("Angles must be finite");
        return Math.Clamp(value, minimum, maximum);
    }

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
}
