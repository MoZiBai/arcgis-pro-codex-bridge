using System.Text.Json;
using ArcGIS.Core.Geometry;

namespace ArcGISCodexBridgeAddin;

internal static class GeometryEngineActions
{
    internal static Task<object> ExecuteAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = (BridgeSupport.String(payload, "operation") ?? string.Empty)
            .Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("operation is required");
        var geometry = ParseGeometry(payload, "geometry", required: true)!;
        var engine = GeometryEngine.Instance;
        object? result = operation switch
        {
            "describe" => DescribeGeometry(geometry),
            "project" => engine.Project(
                geometry,
                SpatialReferenceBuilder.CreateSpatialReference(
                    RequiredInt(payload, "output_wkid"))),
            "buffer" => engine.Buffer(geometry, RequiredDouble(payload, "distance")),
            "geodesic_buffer" => engine.GeodesicBuffer(
                geometry, RequiredDouble(payload, "distance")),
            "boundary" => engine.Boundary(geometry),
            "convex_hull" => engine.ConvexHull(geometry),
            "centroid" => engine.Centroid(geometry),
            "label_point" => engine.LabelPoint(geometry),
            "simplify" => engine.SimplifyAsFeature(
                geometry, BridgeSupport.Bool(payload, "force", true)),
            "simplify_ogc" => engine.SimplifyOgc(
                geometry, BridgeSupport.Bool(payload, "force", true)),
            "generalize" => engine.Generalize(
                geometry,
                RequiredDouble(payload, "max_deviation"),
                BridgeSupport.Bool(payload, "remove_degenerate_parts", true),
                BridgeSupport.Bool(payload, "preserve_curves", true)),
            "densify_by_length" => engine.DensifyByLength(
                geometry, RequiredDouble(payload, "max_segment_length")),
            "move" => engine.Move(
                geometry,
                RequiredDouble(payload, "dx"),
                RequiredDouble(payload, "dy"),
                BridgeSupport.Double(payload, "dz", 0)),
            "rotate" => engine.Rotate(
                geometry,
                ResolveOrigin(payload, geometry),
                RequiredDouble(payload, "rotation_radians")),
            "scale" => engine.Scale(
                geometry,
                ResolveOrigin(payload, geometry),
                RequiredDouble(payload, "scale_x"),
                RequiredDouble(payload, "scale_y"),
                BridgeSupport.Double(payload, "scale_z", 1)),
            "intersection" => engine.Intersection(geometry, RequiredSecond(payload)),
            "union" => engine.Union(geometry, RequiredSecond(payload)),
            "difference" => engine.Difference(geometry, RequiredSecond(payload)),
            "symmetric_difference" => engine.SymmetricDifference(geometry, RequiredSecond(payload)),
            "clip" => engine.Clip(
                geometry,
                RequiredSecond(payload) as Envelope
                    ?? throw new ArgumentException("geometry2 must be an envelope for clip")),
            "contains" => engine.Contains(geometry, RequiredSecond(payload)),
            "within" => engine.Within(geometry, RequiredSecond(payload)),
            "intersects" => engine.Intersects(geometry, RequiredSecond(payload)),
            "disjoint" => engine.Disjoint(geometry, RequiredSecond(payload)),
            "touches" => engine.Touches(geometry, RequiredSecond(payload)),
            "overlaps" => engine.Overlaps(geometry, RequiredSecond(payload)),
            "crosses" => engine.Crosses(geometry, RequiredSecond(payload)),
            "equals" => engine.Equals(geometry, RequiredSecond(payload)),
            "relate" => engine.Relate(
                geometry,
                RequiredSecond(payload),
                BridgeSupport.String(payload, "relation")
                    ?? throw new ArgumentException("relation is required for relate")),
            "distance" => engine.Distance(geometry, RequiredSecond(payload)),
            "distance_3d" => engine.Distance3D(geometry, RequiredSecond(payload)),
            "geodesic_distance" => engine.GeodesicDistance(geometry, RequiredSecond(payload)),
            "area" => engine.Area(geometry),
            "geodesic_area" => engine.GeodesicArea(geometry),
            "length" => engine.Length(geometry),
            "length_3d" => engine.Length3D(geometry),
            "geodesic_length" => engine.GeodesicLength(geometry),
            "nearest_point" => engine.NearestPoint(
                geometry,
                RequiredSecond(payload) as MapPoint
                    ?? throw new ArgumentException("geometry2 must be a point for nearest_point")),
            "multipart_to_singlepart" => engine.MultipartToSinglePart(geometry),
            _ => throw new ArgumentException($"Unsupported geometry operation: {operation}"),
        };
        if (result is null)
            throw new InvalidOperationException($"Geometry operation returned no result: {operation}");
        return Task.FromResult(SerializeResult(operation, geometry, result, payload));
    }

    private static object SerializeResult(
        string operation,
        Geometry input,
        object result,
        JsonElement payload)
    {
        var includeWkt = BridgeSupport.Bool(payload, "include_wkt", false);
        object? materialized = result switch
        {
            Geometry geometry => SerializeGeometry(geometry, includeWkt),
            IEnumerable<Geometry> geometries => geometries
                .Take(Math.Clamp(BridgeSupport.Int(payload, "max_results", 1000), 1, 5000))
                .Select(item => SerializeGeometry(item, includeWkt)).ToArray(),
            ProximityResult proximity => new
            {
                point = SerializeGeometry(proximity.Point, includeWkt),
                distance = Finite(proximity.Distance),
                part_index = proximity.PartIndex,
                point_index = proximity.PointIndex,
                segment_index = proximity.SegmentIndex,
                right_side = proximity.IsRightSide,
            },
            double number => Finite(number),
            _ => result,
        };
        return new
        {
            operation,
            input = DescribeGeometry(input),
            result = materialized,
            source_data_modified = false,
            project_modified = false,
            derived_dataset_created = false,
        };
    }

    private static object SerializeGeometry(Geometry geometry, bool includeWkt)
    {
        var engine = GeometryEngine.Instance;
        return new
        {
            geometry_type = geometry.GeometryType.ToString(),
            esri_json = engine.ExportToJson(JsonExportFlags.JsonExportDefaults, geometry),
            wkt = includeWkt
                ? engine.ExportToWKT(WktExportFlags.WktExportDefaults, geometry)
                : null,
            spatial_reference = geometry.SpatialReference is null ? null : new
            {
                wkid = geometry.SpatialReference.Wkid,
                latest_wkid = geometry.SpatialReference.LatestWkid,
                name = geometry.SpatialReference.Name,
            },
            empty = geometry.IsEmpty,
            has_z = geometry.HasZ,
            has_m = geometry.HasM,
            extent = DescribeEnvelope(geometry.Extent),
        };
    }

    private static object DescribeGeometry(Geometry geometry)
    {
        return new
        {
            geometry_type = geometry.GeometryType.ToString(),
            empty = geometry.IsEmpty,
            has_z = geometry.HasZ,
            has_m = geometry.HasM,
            point_count = geometry.PointCount,
            spatial_reference = geometry.SpatialReference is null ? null : new
            {
                wkid = geometry.SpatialReference.Wkid,
                latest_wkid = geometry.SpatialReference.LatestWkid,
                name = geometry.SpatialReference.Name,
            },
            extent = DescribeEnvelope(geometry.Extent),
        };
    }

    private static object? DescribeEnvelope(Envelope? envelope)
    {
        return envelope is null ? null : new
        {
            xmin = Finite(envelope.XMin),
            ymin = Finite(envelope.YMin),
            xmax = Finite(envelope.XMax),
            ymax = Finite(envelope.YMax),
            zmin = Finite(envelope.ZMin),
            zmax = Finite(envelope.ZMax),
        };
    }

    private static Geometry RequiredSecond(JsonElement payload)
    {
        return ParseGeometry(payload, "geometry2", required: true)!;
    }

    private static Geometry? ParseGeometry(JsonElement payload, string name, bool required)
    {
        if (!payload.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (required)
                throw new ArgumentException($"{name} is required");
            return null;
        }
        var text = value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();
        if (text.Length > 2 * 1024 * 1024)
            throw new ArgumentException($"{name} is limited to 2 MB");
        var format = (BridgeSupport.String(payload, name + "_format", "json") ?? "json")
            .Trim().ToLowerInvariant();
        if (format == "wkt")
        {
            SpatialReference? spatialReference = null;
            var wkidName = name + "_wkid";
            if (payload.TryGetProperty(wkidName, out var wkid) && wkid.TryGetInt32(out var code))
                spatialReference = SpatialReferenceBuilder.CreateSpatialReference(code);
            return GeometryEngine.Instance.ImportFromWKT(
                WktImportFlags.WktImportNonTrusted, text, spatialReference);
        }
        if (format != "json")
            throw new ArgumentException($"{name}_format must be json or wkt");
        return GeometryEngine.Instance.ImportFromJson(JsonImportFlags.JsonImportDefaults, text);
    }

    private static MapPoint ResolveOrigin(JsonElement payload, Geometry geometry)
    {
        var explicitOrigin = ParseGeometry(payload, "origin", required: false);
        if (explicitOrigin is MapPoint point)
            return point;
        if (explicitOrigin is not null)
            throw new ArgumentException("origin must be a point geometry");
        return GeometryEngine.Instance.Centroid(geometry);
    }

    private static double RequiredDouble(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            throw new ArgumentException($"{name} is required and must be numeric");
        var result = value.GetDouble();
        if (!double.IsFinite(result))
            throw new ArgumentException($"{name} must be finite");
        return result;
    }

    private static int RequiredInt(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result))
            throw new ArgumentException($"{name} is required and must be an integer");
        return result;
    }

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
}
