using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class SceneViewActions
{
    internal static async Task<object> GetMapViewStateAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(() => DescribeActiveView(requestedMap));
    }

    internal static async Task<object> ConfigureMapViewAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(() =>
        {
            var view = ResolveActiveView(requestedMap);
            if (payload.TryGetProperty("viewing_mode", out _))
            {
                var mode = ParseEnum<MapViewingMode>(
                    BridgeSupport.String(payload, "viewing_mode"), "viewing_mode");
                if (!view.CanSetViewingMode(mode))
                {
                    throw new InvalidOperationException(
                        $"The active view cannot use viewing mode {mode}");
                }
                view.SetViewingMode(mode);
            }
            if (payload.TryGetProperty("scene_drawing_mode", out _))
            {
                if (!view.Map.IsScene)
                {
                    throw new InvalidOperationException("scene_drawing_mode requires an active scene");
                }
                view.SetSceneDrawingMode(ParseEnum<SceneDrawingMode>(
                    BridgeSupport.String(payload, "scene_drawing_mode"), "scene_drawing_mode"));
            }
            if (payload.TryGetProperty("field_of_view", out var fieldOfView)
                && fieldOfView.ValueKind == JsonValueKind.Number)
            {
                view.SetFieldOfView(Math.Clamp(fieldOfView.GetDouble(), 1, 170));
            }
            if (BridgeSupport.Bool(payload, "clear_camera_effect"))
            {
                view.SetCameraEffect(null!);
            }
            else if (payload.TryGetProperty("camera_effect_preset", out _))
            {
                view.SetCameraEffect(CreateCameraEffectPreset(payload));
            }
            else if (payload.TryGetProperty("camera_effect", out var cameraEffect)
                && cameraEffect.ValueKind == JsonValueKind.Object)
            {
                view.SetCameraEffect(ParseEffect<CIMCameraEffect>(cameraEffect));
            }
            if (BridgeSupport.Bool(payload, "clear_visual_effect"))
            {
                view.SetVisualEffect(null!);
            }
            else if (payload.TryGetProperty("visual_effect_preset", out _))
            {
                view.SetVisualEffect(CreateVisualEffectPreset(payload));
            }
            else if (payload.TryGetProperty("visual_effect", out var visualEffect)
                && visualEffect.ValueKind == JsonValueKind.Object)
            {
                view.SetVisualEffect(ParseEffect<CIMVisualEffect>(visualEffect));
            }
            if (payload.TryGetProperty("postprocessing_effects", out var postprocessing)
                && postprocessing.ValueKind == JsonValueKind.Array)
            {
                var effects = postprocessing.EnumerateArray()
                    .Select(ParseEffect<CIMPostprocessingEffect>)
                    .ToArray();
                view.SetPostprocessingEffects(effects);
            }
            else if (payload.TryGetProperty("postprocessing_presets", out var presets)
                && presets.ValueKind == JsonValueKind.Array)
            {
                view.SetPostprocessingEffects(presets.EnumerateArray()
                    .Select(item => CreatePostprocessingEffectPreset(
                        item.GetString() ?? string.Empty, payload))
                    .ToArray());
            }
            return DescribeActiveView(requestedMap);
        });
    }

    internal static async Task<object> ListElevationSurfacesAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var includeCim = BridgeSupport.Bool(payload, "include_cim", false);
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            return (object)new
            {
                map = map.Name,
                is_scene = map.IsScene,
                surfaces = map.GetElevationSurfaceLayers().Select(surface =>
                {
                    var definition = surface.GetDefinition();
                    return new
                    {
                        name = surface.Name,
                        uri = surface.URI,
                        is_ground = surface == map.GetGroundElevationSurfaceLayer(),
                        visible = surface.IsVisible,
                        transparency = surface.Transparency,
                        vertical_exaggeration = surface.VerticalExaggeration,
                        enable_surface_shading = surface.EnableSurfaceShading,
                        tin_shading_mode = surface.SurfaceTINShadingMode.ToString(),
                        elevation_mode = surface.ElevationMode.ToString(),
                        source_count = surface.Layers.Count,
                        cim_type = definition.GetType().Name,
                        cim_json = includeCim ? definition.ToJson() : null,
                    };
                }).ToArray(),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> ConfigureElevationSurfaceAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var surfaceName = BridgeSupport.String(payload, "surface_name");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var surface = ResolveSurface(map, surfaceName);
            if (payload.TryGetProperty("visible", out _))
                surface.SetVisibility(BridgeSupport.Bool(payload, "visible", true));
            if (payload.TryGetProperty("transparency", out _))
                surface.SetTransparency(Math.Clamp(BridgeSupport.Double(payload, "transparency"), 0, 100));
            if (payload.TryGetProperty("vertical_exaggeration", out _))
                surface.SetVerticalExaggeration(Math.Max(0, BridgeSupport.Double(payload, "vertical_exaggeration", 1)));
            if (payload.TryGetProperty("enable_surface_shading", out _))
                surface.SetEnableSurfaceShading(BridgeSupport.Bool(payload, "enable_surface_shading", true));
            if (payload.TryGetProperty("tin_shading_mode", out _))
                surface.SetSurfaceTINShadingMode(ParseEnum<SurfaceTINShadingMode>(
                    BridgeSupport.String(payload, "tin_shading_mode"), "tin_shading_mode"));
            if (payload.TryGetProperty("navigate_below_ground", out _))
                surface.SetEnableNavigationBelowGound(BridgeSupport.Bool(payload, "navigate_below_ground"));
            if (payload.TryGetProperty("color", out _))
                surface.SetCIMColor(BridgeSupport.Color(payload, "color"));
            var definition = surface.GetDefinition();
            return (object)new
            {
                map = map.Name,
                surface = surface.Name,
                visible = surface.IsVisible,
                transparency = surface.Transparency,
                vertical_exaggeration = surface.VerticalExaggeration,
                enable_surface_shading = surface.EnableSurfaceShading,
                tin_shading_mode = surface.SurfaceTINShadingMode.ToString(),
                cim_type = definition.GetType().Name,
                cim_json = definition.ToJson(),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> ConfigureLayerElevationAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var layerName = Required(payload, "layer_name");
        var operation = BridgeSupport.String(payload, "operation", "set")!.ToLowerInvariant();
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = BridgeSupport.ResolveLayer(map, layerName);
            var definition = layer.GetElevationTypeDefinition();
            if (operation == "set")
            {
                if (payload.TryGetProperty("elevation_type", out _))
                {
                    definition.ElevationType = ParseEnum<LayerElevationType>(
                        BridgeSupport.String(payload, "elevation_type"), "elevation_type");
                }
                var surfaceName = BridgeSupport.String(payload, "custom_surface_name");
                if (!string.IsNullOrWhiteSpace(surfaceName))
                {
                    definition.CustomSurfaceURI = ResolveSurface(map, surfaceName).URI;
                }
                if (payload.TryGetProperty("vertical_exaggeration", out var exaggeration)
                    && exaggeration.ValueKind == JsonValueKind.Number)
                    definition.VerticalExaggeration = Math.Max(0, exaggeration.GetDouble());
                if (payload.TryGetProperty("cartographic_offset", out var offset)
                    && offset.ValueKind == JsonValueKind.Number)
                    definition.CartographicOffset = offset.GetDouble();
                if (payload.TryGetProperty("numeric_field_name", out _))
                    definition.NumericFieldName = BridgeSupport.String(payload, "numeric_field_name");
                if (payload.TryGetProperty("feature_elevation_expression", out _))
                    definition.FeatureElevationExpression = BridgeSupport.String(payload, "feature_elevation_expression");
                if (payload.TryGetProperty("offset_unit", out _))
                    definition.CartographicOffsetUnits = ParseLinearUnit(
                        BridgeSupport.String(payload, "offset_unit"));
                if (!layer.CanSetElevationTypeDefinition(definition))
                {
                    throw new InvalidOperationException(
                        $"Layer does not support the requested elevation definition: {layer.Name}");
                }
                layer.SetElevationTypeDefinition(definition);
                definition = layer.GetElevationTypeDefinition();
            }
            else if (operation != "inspect")
            {
                throw new ArgumentException("operation must be inspect or set");
            }
            return (object)new
            {
                map = map.Name,
                layer = layer.Name,
                elevation_type = definition.ElevationType.ToString(),
                custom_surface_uri = definition.CustomSurfaceURI,
                vertical_exaggeration = definition.VerticalExaggeration,
                cartographic_offset = definition.CartographicOffset,
                offset_unit = definition.CartographicOffsetUnits?.Name,
                numeric_field_name = definition.NumericFieldName,
                feature_elevation_expression = definition.FeatureElevationExpression,
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> SampleElevationAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var surfaceName = BridgeSupport.String(payload, "surface_name");
        if (!payload.TryGetProperty("points", out var points)
            || points.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("points array is required");
        }
        var coordinates = points.EnumerateArray().Select(item => new
        {
            x = BridgeSupport.Double(item, "x"),
            y = BridgeSupport.Double(item, "y"),
        }).ToArray();
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var surface = ResolveSurface(map, surfaceName);
            var samples = coordinates.Select(point =>
            {
                double? z = null;
                var status = "Error";
                string? error = null;
                try
                {
                    var result = surface.GetZs(point.x, point.y);
                    var sampled = result.Geometry as MapPoint;
                    z = sampled is not null && double.IsFinite(sampled.Z) ? sampled.Z : null;
                    status = result.Status.ToString();
                }
                catch (Exception exception)
                {
                    error = exception.Message;
                }
                return new
                {
                    point.x,
                    point.y,
                    z,
                    status,
                    error,
                };
            }).ToArray();
            return (object)new
            {
                map = map.Name,
                surface = surface.Name,
                samples,
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> GetElevationProfileAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var surfaceName = BridgeSupport.String(payload, "surface_name");
        var start = RequiredObject(payload, "start");
        var end = RequiredObject(payload, "end");
        var count = Math.Clamp(BridgeSupport.Int(payload, "num_points", 100), 2, 10000);
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var surface = ResolveSurface(map, surfaceName);
            var startPoint = MapPointBuilderEx.CreateMapPoint(
                BridgeSupport.Double(start, "x"), BridgeSupport.Double(start, "y"), map.SpatialReference);
            var endPoint = MapPointBuilderEx.CreateMapPoint(
                BridgeSupport.Double(end, "x"), BridgeSupport.Double(end, "y"), map.SpatialReference);
            try
            {
                var result = map.GetElevationProfileFromSurface(startPoint, endPoint, count, surface);
                var profile = result.Polyline?.Points.Select((point, index) => new
                {
                    index,
                    x = point.X,
                    y = point.Y,
                    z = double.IsFinite(point.Z) ? point.Z : (double?)null,
                }).ToArray() ?? Array.Empty<object>();
                return (object)new
                {
                    map = map.Name,
                    surface = surface.Name,
                    status = result.Status.ToString(),
                    points = profile,
                    error = (string?)null,
                    source_data_modified = false,
                };
            }
            catch (Exception exception)
            {
                return (object)new
                {
                    map = map.Name,
                    surface = surface.Name,
                    status = "Error",
                    points = Array.Empty<object>(),
                    error = exception.Message,
                    source_data_modified = false,
                };
            }
        });
    }

    internal static async Task<object> GetAnimationStateAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(() => DescribeAnimation(requestedMap));
    }

    internal static async Task<object> ControlAnimationAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var operation = Required(payload, "operation").ToLowerInvariant();
        return await QueuedTask.Run<object>(() =>
        {
            var view = ResolveActiveView(requestedMap);
            var mapAnimation = view.Map.Animation;
            var player = view.Animation;
            if (mapAnimation is null || player is null)
            {
                if (operation is not "stop" and not "stop_export")
                    throw new InvalidOperationException("The active map has no animation");
                return DescribeAnimation(requestedMap);
            }
            switch (operation)
            {
                case "play":
                    player.Play();
                    break;
                case "stop":
                    player.Stop();
                    break;
                case "next_keyframe":
                    player.NextKeyframe();
                    break;
                case "previous_keyframe":
                    player.PreviousKeyframe();
                    break;
                case "scale_duration":
                    mapAnimation.ScaleDuration(Math.Max(
                        0.001, BridgeSupport.Double(payload, "factor", 1)));
                    break;
                case "stop_export":
                    player.StopExport();
                    break;
                default:
                    throw new ArgumentException(
                        "operation must be play, stop, next_keyframe, previous_keyframe, scale_duration, or stop_export");
            }
            return DescribeAnimation(requestedMap);
        });
    }

    internal static async Task<object> BeginAnimationExportAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var output = Required(payload, "output_file");
        return await QueuedTask.Run<object>(() =>
        {
            var view = ResolveActiveView(requestedMap);
            var animationName = BridgeSupport.String(payload, "animation_name")
                ?? view.Map.Animation.AnimationNames.FirstOrDefault()
                ?? throw new InvalidOperationException("The active map has no animation to export");
            if (!view.Map.Animation.AnimationNames.Contains(animationName, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Animation not found: {animationName}");
            }
            var parameters = new AnimationExportParameters
            {
                FilePath = output,
                ResolutionWidth = Math.Clamp(BridgeSupport.Int(payload, "width", 1920), 64, 16384),
                ResolutionHeight = Math.Clamp(BridgeSupport.Int(payload, "height", 1080), 64, 16384),
                FrameRate = Math.Clamp(BridgeSupport.Double(payload, "frame_rate", 30), 1, 240),
                Quality = Math.Clamp(BridgeSupport.Double(payload, "quality", 75), 1, 100),
                StartFrame = Math.Max(0, BridgeSupport.Int(payload, "start_frame", 0)),
                EndFrame = Math.Max(0, BridgeSupport.Int(
                    payload, "end_frame", (int)Math.Max(0, view.Map.Animation.NumberOfFrames - 1))),
            };
            var started = view.Animation.BeginExport(animationName, parameters);
            return (object)new
            {
                map = view.Map.Name,
                animation = animationName,
                output,
                started,
                is_exporting = view.Animation.GetIsExporting(),
                source_data_modified = false,
            };
        });
    }

    private static object DescribeActiveView(string? requestedMap)
    {
        var view = ResolveActiveView(requestedMap);
        var cameraEffect = view.GetCameraEffect();
        var visualEffect = view.GetVisualEffect();
        return new
        {
            map = view.Map.Name,
            is_scene = view.Map.IsScene,
            viewing_mode = view.ViewingMode.ToString(),
            scene_drawing_mode = view.SceneDrawingMode.ToString(),
            field_of_view = view.FieldOfView,
            active_range_name = view.ActiveRangeName,
            camera_effect = DescribeCim(cameraEffect),
            visual_effect = DescribeCim(visualEffect),
            postprocessing_effects = view.GetPostprocessingEffects()
                .Select(DescribeCim).ToArray(),
            source_data_modified = false,
        };
    }

    private static object DescribeAnimation(string? requestedMap)
    {
        var view = ResolveActiveView(requestedMap);
        var animation = view.Map.Animation;
        var player = view.Animation;
        if (animation is null || player is null)
        {
            return new
            {
                map = view.Map.Name,
                animation_names = Array.Empty<string>(),
                current_animation = (string?)null,
                duration_seconds = 0d,
                number_of_frames = 0L,
                current_frame = 0L,
                current_time_seconds = 0d,
                is_exporting = false,
                tracks = Array.Empty<object>(),
                source_data_modified = false,
            };
        }
        return new
        {
            map = view.Map.Name,
            animation_names = animation.AnimationNames,
            current_animation = animation.CurrentAnimationName,
            duration_seconds = animation.Duration.TotalSeconds,
            number_of_frames = animation.NumberOfFrames,
            current_frame = player.CurrentFrame,
            current_time_seconds = player.CurrentTime.TotalSeconds,
            is_exporting = player.GetIsExporting(),
            tracks = animation.Tracks.Select(track => new
            {
                animation_name = track.AnimationName,
                keyframes = track.Keyframes.Select((keyframe, index) => new
                {
                    index,
                    time_seconds = keyframe.TrackTime.TotalSeconds,
                    type = keyframe.GetType().Name,
                }).ToArray(),
            }).ToArray(),
            source_data_modified = false,
        };
    }

    private static MapView ResolveActiveView(string? requestedMap)
    {
        var view = MapView.Active
            ?? throw new InvalidOperationException("There is no active map view");
        if (!string.IsNullOrWhiteSpace(requestedMap)
            && !string.Equals(view.Map.Name, requestedMap, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The requested map must be the active map view");
        }
        return view;
    }

    private static ElevationSurfaceLayer ResolveSurface(Map map, string? surfaceName)
    {
        if (string.IsNullOrWhiteSpace(surfaceName))
        {
            return map.GetGroundElevationSurfaceLayer()
                ?? throw new InvalidOperationException("The map has no ground elevation surface");
        }
        return map.GetElevationSurfaceLayers().FirstOrDefault(surface => string.Equals(
            surface.Name, surfaceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Elevation surface not found: {surfaceName}");
    }

    private static T ParseEffect<T>(JsonElement config) where T : class
    {
        var type = BridgeSupport.String(config, "cim_type")
            ?? throw new ArgumentException("Effect cim_type is required");
        var json = BridgeSupport.String(config, "cim_json")
            ?? throw new ArgumentException("Effect cim_json is required");
        return CimObjectActions.ParseNamedType<T>(type, json);
    }

    private static CIMCameraEffect CreateCameraEffectPreset(JsonElement payload)
    {
        var preset = Required(payload, "camera_effect_preset").ToLowerInvariant();
        var parameters = OptionalObject(payload, "effect_parameters");
        return preset switch
        {
            "depth_of_field" => new CIMDepthOfFieldEffect
            {
                IsActive = true,
                FocusDepth = BridgeSupport.Double(parameters, "focus_depth", 0.5),
                FocusDistance = BridgeSupport.Double(parameters, "focus_distance", 1000),
            },
            "tilt_shift" => new CIMTiltShiftEffect
            {
                IsActive = true,
                BlurStrength = BridgeSupport.Double(parameters, "blur_strength", 0.5),
                LeftOffset = BridgeSupport.Double(parameters, "left_offset", 0.2),
                RightOffset = BridgeSupport.Double(parameters, "right_offset", 0.8),
                TopOffset = BridgeSupport.Double(parameters, "top_offset", 0.2),
                BottomOffset = BridgeSupport.Double(parameters, "bottom_offset", 0.8),
            },
            _ => throw new ArgumentException(
                "camera_effect_preset must be depth_of_field or tilt_shift"),
        };
    }

    private static CIMVisualEffect CreateVisualEffectPreset(JsonElement payload)
    {
        var preset = Required(payload, "visual_effect_preset").ToLowerInvariant();
        var parameters = OptionalObject(payload, "effect_parameters");
        return preset switch
        {
            "black_and_white" => new CIMBlackAndWhiteEffect { IsActive = true },
            "blueprint" => new CIMBlueprintEffect
            {
                IsActive = true,
                GridSize = BridgeSupport.Double(parameters, "size", 20),
                GridSubdivisions = Math.Max(1, BridgeSupport.Int(parameters, "subdivisions", 4)),
                OutlineStrength = BridgeSupport.Double(parameters, "strength", 0.5),
            },
            "cross_mosaic" => new CIMCrossMosaicEffect
            {
                IsActive = true,
                Size = BridgeSupport.Double(parameters, "size", 8),
            },
            "hex_mosaic" => new CIMHexMosaicEffect
            {
                IsActive = true,
                Size = BridgeSupport.Double(parameters, "size", 8),
            },
            "pixelated" => new CIMPixelatedEffect
            {
                IsActive = true,
                PixelSize = BridgeSupport.Double(parameters, "size", 8),
                ColorFactor = Math.Max(1, BridgeSupport.Int(parameters, "color_factor", 8)),
            },
            "halftone" => new CIMHalftoneEffect
            {
                IsActive = true,
                DotSize = BridgeSupport.Double(parameters, "size", 3),
                DotStrength = BridgeSupport.Double(parameters, "strength", 0.5),
                InvertTone = BridgeSupport.Bool(parameters, "invert", false),
            },
            "outline" => new CIMOutlineEffect
            {
                IsActive = true,
                OutlineStrength = BridgeSupport.Double(parameters, "strength", 0.5),
                OutlineColor = BridgeSupport.Color(parameters, "outline_color"),
                BackgroundColor = BridgeSupport.Color(
                    parameters, "background_color", ColorFactory.Instance.CreateRGBColor(255, 255, 255)),
            },
            "pencil_sketch" => new CIMPencilSketchEffect
            {
                IsActive = true,
                Grayscale = BridgeSupport.Bool(parameters, "grayscale", true),
                CrosshatchStrength = BridgeSupport.Double(parameters, "strength", 0.5),
                CrosshatchAngleCount = Math.Clamp(BridgeSupport.Int(parameters, "angle_count", 2), 1, 8),
            },
            "toon" => new CIMToonEffect
            {
                IsActive = true,
                LineStrength = BridgeSupport.Double(parameters, "strength", 0.5),
            },
            "watercolor" => new CIMWatercolorEffect
            {
                IsActive = true,
                BackgroundColor = BridgeSupport.Color(
                    parameters, "background_color", ColorFactory.Instance.CreateRGBColor(245, 245, 240)),
                MarginGradient = BridgeSupport.Double(parameters, "margin_gradient", 0.5),
                MarginWidth = BridgeSupport.Double(parameters, "margin_width", 0.2),
                DrawMarginOutlines = BridgeSupport.Bool(parameters, "draw_margin_outlines", true),
            },
            "monochromatic" => new CIMMonochromaticEffect
            {
                IsActive = true,
                Color = BridgeSupport.Color(parameters, "color", ColorFactory.Instance.CreateRGBColor(70, 100, 120)),
            },
            _ => throw new ArgumentException("Unsupported visual_effect_preset"),
        };
    }

    private static CIMPostprocessingEffect CreatePostprocessingEffectPreset(
        string preset,
        JsonElement payload)
    {
        var parameters = OptionalObject(payload, "effect_parameters");
        return preset.ToLowerInvariant() switch
        {
            "bloom" => new CIMBloomPostprocessingEffect
            {
                IsActive = true,
                Strength = BridgeSupport.Double(parameters, "strength", 0.5),
                Threshold = BridgeSupport.Double(parameters, "threshold", 0.7),
            },
            "vignette" => new CIMVignettePostprocessingEffect
            {
                IsActive = true,
                Size = BridgeSupport.Double(parameters, "size", 0.5),
                Strength = BridgeSupport.Double(parameters, "strength", 0.5),
            },
            "grain" => new CIMGrainPostprocessingEffect
            {
                IsActive = true,
                Size = BridgeSupport.Double(parameters, "size", 0.5),
                Strength = BridgeSupport.Double(parameters, "strength", 0.25),
            },
            "color_grading" => new CIMColorGradingPostprocessingEffect
            {
                IsActive = true,
                Brightness = BridgeSupport.Int(parameters, "brightness", 0),
                Contrast = BridgeSupport.Int(parameters, "contrast", 0),
                Saturation = BridgeSupport.Int(parameters, "saturation", 0),
            },
            _ => throw new ArgumentException(
                "postprocessing preset must be bloom, vignette, grain, or color_grading"),
        };
    }

    private static object? DescribeCim(CIMObject? value)
    {
        return value is null ? null : new
        {
            cim_type = value.GetType().Name,
            cim_json = value.ToJson(),
        };
    }

    private static T ParseEnum<T>(string? value, string property) where T : struct, Enum
    {
        return Enum.TryParse<T>(value, true, out var parsed)
            ? parsed
            : throw new ArgumentException(
                $"Unsupported {property}: {value}. Expected one of: {string.Join(", ", Enum.GetNames<T>())}");
    }

    private static LinearUnit ParseLinearUnit(string? value)
    {
        return (value ?? string.Empty).ToLowerInvariant() switch
        {
            "meter" or "meters" or "metre" or "metres" => LinearUnit.Meters,
            "kilometer" or "kilometers" or "kilometre" or "kilometres" => LinearUnit.Kilometers,
            "foot" or "feet" => LinearUnit.Feet,
            "mile" or "miles" => LinearUnit.Miles,
            _ => throw new ArgumentException("offset_unit must be meters, kilometers, feet, or miles"),
        };
    }

    private static string Required(JsonElement payload, string name)
    {
        return BridgeSupport.String(payload, name)
            ?? throw new ArgumentException($"{name} is required");
    }

    private static JsonElement RequiredObject(JsonElement payload, string name)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }
        throw new ArgumentException($"{name} object is required");
    }

    private static JsonElement OptionalObject(JsonElement payload, string name)
    {
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Object
                ? value
                : default;
    }
}
