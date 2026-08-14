using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class SymbologyActions
{
    internal static object DescribeRasterColorizer(CIMRasterColorizer? colorizer)
    {
        if (colorizer is null)
        {
            return new { type = (string?)null };
        }
        if (colorizer is CIMRasterStretchColorizer stretch)
        {
            return new
            {
                type = stretch.GetType().Name,
                heading = stretch.Heading,
                stretch_type = stretch.StretchType.ToString(),
                use_custom_min_max = stretch.UseCustomStretchMinMax,
                minimum = stretch.UseCustomStretchMinMax ? stretch.CustomStretchMin : (double?)null,
                maximum = stretch.UseCustomStretchMinMax ? stretch.CustomStretchMax : (double?)null,
                min_percent = stretch.MinPercent,
                max_percent = stretch.MaxPercent,
                gamma = stretch.GammaValue,
                color_ramp = DescribeColorRamp(stretch.ColorRamp),
            };
        }
        if (colorizer is CIMRasterClassifyColorizer classify)
        {
            return new
            {
                type = classify.GetType().Name,
                heading = classify.Heading,
                field = classify.Field,
                classification_method = classify.ClassificationMethod.ToString(),
                minimum_break = classify.MinimumBreak,
                class_breaks = classify.ClassBreaks?.Select(item => new
                {
                    upper_bound = item.UpperBound,
                    label = item.Label,
                    color = ColorValues(item.Color),
                }).ToArray(),
                color_ramp = DescribeColorRamp(classify.ColorRamp),
            };
        }
        if (colorizer is CIMRasterShadedReliefColorizer relief)
        {
            return new
            {
                type = relief.GetType().Name,
                z_factor = relief.ZFactor,
                altitude = relief.Altitude,
                azimuth = relief.Azimuth,
                use_map_illumination = relief.UseMapIlluminationProperties,
                color_ramp = DescribeColorRamp(relief.ColorRamp),
            };
        }
        if (colorizer is CIMRasterRGBColorizer rgb)
        {
            return new
            {
                type = rgb.GetType().Name,
                red_band_index = rgb.RedBandIndex,
                green_band_index = rgb.GreenBandIndex,
                blue_band_index = rgb.BlueBandIndex,
                alpha_band_index = rgb.UseAlphaBand ? rgb.AlphaBandIndex : (int?)null,
                stretch_type = rgb.StretchType.ToString(),
                gamma = new[] { rgb.GammaR, rgb.GammaG, rgb.GammaB },
                min_percent = rgb.MinPercent,
                max_percent = rgb.MaxPercent,
                use_custom_min_max = rgb.UseCustomStretchMinMax,
            };
        }
        if (colorizer is CIMRasterUniqueValueColorizer unique)
        {
            return new
            {
                type = unique.GetType().Name,
                field = unique.FieldName,
                group_count = unique.Groups?.Length ?? 0,
                color_ramp = DescribeColorRamp(unique.ColorRamp),
            };
        }
        if (colorizer is CIMRasterColorMapColorizer colormap)
        {
            return new
            {
                type = colormap.GetType().Name,
                values = colormap.Values,
                labels = colormap.Labels,
                colors = colormap.Colors?.Select(ColorValues).ToArray(),
            };
        }
        if (colorizer is CIMRasterVectorFieldColorizer vectorField)
        {
            return new
            {
                type = vectorField.GetType().Name,
                magnitude_band_index = vectorField.MagnitudeBandIndex,
                direction_band_index = vectorField.DirectionBandIndex,
                is_uv_components = vectorField.IsUVComponents,
                symbolization_type = vectorField.SymbolizationType.ToString(),
                minimum_magnitude = vectorField.MinimumMagnitude,
                maximum_magnitude = vectorField.MaximumMagnitude,
            };
        }
        if (colorizer is CIMRasterDiscreteColorColorizer discrete)
        {
            return new
            {
                type = discrete.GetType().Name,
                color_count = discrete.NumColors,
                color_ramp = DescribeColorRamp(discrete.ColorRamp),
            };
        }
        return new
        {
            type = colorizer.GetType().Name,
            cim_json = colorizer.ToJson(),
        };
    }

    internal static object DescribeColorRamp(CIMColorRamp? ramp)
    {
        if (ramp is CIMMultipartColorRamp multipart)
        {
            return new
            {
                type = multipart.GetType().Name,
                weights = multipart.Weights,
                ramps = multipart.ColorRamps?.Select(DescribeColorRamp).ToArray(),
            };
        }
        if (ramp is CIMContinuousColorRamp continuous)
        {
            return new
            {
                type = continuous.GetType().Name,
                from = ColorValues(continuous.FromColor),
                to = ColorValues(continuous.ToColor),
            };
        }
        if (ramp is CIMFixedColorRamp fixedRamp)
        {
            return new
            {
                type = fixedRamp.GetType().Name,
                colors = fixedRamp.Colors?.Select(ColorValues).ToArray(),
            };
        }
        return new
        {
            type = ramp?.GetType().Name,
            cim_json = ramp?.ToJson(),
        };
    }

    internal static double[]? ColorValues(CIMColor? color)
    {
        return color is null
            ? null
            : ColorFactory.Instance.ConvertToRGB(color).Values?.ToArray();
    }

    internal static async Task<object> ApplyVectorAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var rendererType = BridgeSupport.String(payload, "renderer", "simple")!.ToLowerInvariant();

        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = BridgeSupport.ResolveLayer(map, layerName) as FeatureLayer
                ?? throw new InvalidOperationException($"Feature layer not found: {layerName}");
            var symbolConfig = payload.TryGetProperty("symbol", out var symbolElement)
                && symbolElement.ValueKind == JsonValueKind.Object
                ? symbolElement
                : default;
            var symbol = BridgeSupport.FeatureSymbol(layer, symbolConfig);
            RendererDefinition definition;

            switch (rendererType)
            {
                case "simple":
                    definition = new SimpleRendererDefinition(
                        symbol.MakeSymbolReference(),
                        BridgeSupport.String(payload, "label", layer.Name),
                        BridgeSupport.String(payload, "description", string.Empty));
                    break;

                case "unique":
                case "unique_value":
                {
                    var fields = BridgeSupport.Strings(payload, "fields");
                    var singleField = BridgeSupport.String(payload, "field");
                    if (fields.Length == 0 && !string.IsNullOrWhiteSpace(singleField))
                    {
                        fields = new[] { singleField };
                    }
                    if (fields.Length == 0)
                    {
                        throw new ArgumentException("field or fields is required for unique value symbology");
                    }
                    definition = new UniqueValueRendererDefinition
                    {
                        ValueFields = fields.ToList(),
                        ColorRamp = BridgeSupport.ColorRamp(payload),
                        SymbolTemplate = symbol.MakeSymbolReference(),
                        ValuesLimit = Math.Max(0, BridgeSupport.Int(payload, "values_limit", 100)),
                        UseDefaultSymbol = true,
                        DefaultSymbol = symbol.MakeSymbolReference(),
                    };
                    break;
                }

                case "graduated":
                case "graduated_colors":
                case "class_breaks":
                {
                    var field = BridgeSupport.String(payload, "field")
                        ?? throw new ArgumentException("field is required for graduated symbology");
                    definition = new GraduatedColorsRendererDefinition(
                        field,
                        BridgeSupport.Classification(BridgeSupport.String(payload, "classification", "NaturalBreaks")),
                        Math.Clamp(BridgeSupport.Int(payload, "class_count", 5), 2, 32),
                        BridgeSupport.ColorRamp(payload),
                        symbol.MakeSymbolReference())
                    {
                        NormalizationField = BridgeSupport.String(payload, "normalization_field"),
                        NormalizationType = Normalization(payload),
                        ExclusionClause = BridgeSupport.String(payload, "exclusion_clause"),
                    };
                    break;
                }

                case "graduated_symbol":
                case "graduated_symbols":
                {
                    var field = RequiredField(payload, rendererType);
                    var minimumSize = Math.Clamp(BridgeSupport.Double(payload, "minimum_size", 4), 0.1, 200);
                    var maximumSize = Math.Max(
                        minimumSize,
                        Math.Clamp(BridgeSupport.Double(payload, "maximum_size", 24), 0.1, 400));
                    definition = new GraduatedSymbolsRendererDefinition(
                        field,
                        BridgeSupport.Classification(BridgeSupport.String(payload, "classification", "NaturalBreaks")),
                        Math.Clamp(BridgeSupport.Int(payload, "class_count", 5), 2, 32),
                        BridgeSupport.ColorRamp(payload),
                        symbol.MakeSymbolReference(),
                        minimumSize,
                        maximumSize)
                    {
                        NormalizationField = BridgeSupport.String(payload, "normalization_field"),
                        NormalizationType = Normalization(payload),
                        ExclusionClause = BridgeSupport.String(payload, "exclusion_clause"),
                    };
                    break;
                }

                case "proportional":
                case "proportional_symbols":
                {
                    var minimumSize = Math.Clamp(BridgeSupport.Double(payload, "minimum_size", 4), 0.1, 200);
                    var maximumSize = Math.Max(
                        minimumSize,
                        Math.Clamp(BridgeSupport.Double(payload, "maximum_size", 24), 0.1, 400));
                    definition = new ProportionalRendererDefinition(
                        RequiredField(payload, rendererType),
                        symbol.MakeSymbolReference(),
                        minimumSize,
                        maximumSize,
                        BridgeSupport.Bool(payload, "upper_bounded", true))
                    {
                        NormalizationField = BridgeSupport.String(payload, "normalization_field"),
                        NormalizationType = Normalization(payload),
                        FlanneryCompensation = BridgeSupport.Bool(payload, "flannery_compensation", true),
                        LegendSymbolCount = Math.Clamp(BridgeSupport.Int(payload, "legend_symbol_count", 3), 1, 10),
                        ShowNullValues = BridgeSupport.Bool(payload, "show_null_values"),
                        LowerSizeStop = OptionalDouble(payload, "lower_size_stop"),
                        UpperSizeStop = OptionalDouble(payload, "upper_size_stop"),
                        ExclusionClause = BridgeSupport.String(payload, "exclusion_clause"),
                    };
                    break;
                }

                case "unclassed":
                case "unclassed_colors":
                {
                    definition = new UnclassedColorsRendererDefinition(
                        RequiredField(payload, rendererType),
                        symbol.MakeSymbolReference(),
                        BridgeSupport.ColorRamp(payload))
                    {
                        NormalizationField = BridgeSupport.String(payload, "normalization_field"),
                        LowerColorStop = OptionalDouble(payload, "lower_color_stop"),
                        UpperColorStop = OptionalDouble(payload, "upper_color_stop"),
                        LowerLabel = BridgeSupport.String(payload, "lower_label"),
                        UpperLabel = BridgeSupport.String(payload, "upper_label"),
                        ShowNullValues = BridgeSupport.Bool(payload, "show_null_values"),
                    };
                    break;
                }

                case "heatmap":
                case "heat_map":
                {
                    if (layer.ShapeType is not esriGeometryType.esriGeometryPoint
                        and not esriGeometryType.esriGeometryMultipoint)
                    {
                        throw new InvalidOperationException("Heat-map symbology requires a point or multipoint layer.");
                    }
                    definition = new HeatMapRendererDefinition
                    {
                        ColorRamp = BridgeSupport.ColorRamp(payload),
                        WeightField = BridgeSupport.String(payload, "weight_field", BridgeSupport.String(payload, "field", string.Empty)),
                        Radius = Math.Clamp(BridgeSupport.Int(payload, "radius", 15), 1, 200),
                        RendereringQuality = Math.Clamp(BridgeSupport.Int(payload, "rendering_quality", 2), 1, 3),
                        LowerLabel = BridgeSupport.String(payload, "lower_label", "Low"),
                        UpperLabel = BridgeSupport.String(payload, "upper_label", "High"),
                    };
                    break;
                }

                case "dot_density":
                {
                    if (layer.ShapeType is not esriGeometryType.esriGeometryPolygon)
                    {
                        throw new InvalidOperationException("Dot-density symbology requires a polygon layer.");
                    }
                    var fields = RendererFields(payload, "value_fields");
                    if (fields.Count == 0)
                    {
                        throw new ArgumentException("value_fields or fields is required for dot-density symbology");
                    }
                    var dotDensity = new DotDensityRendererDefinition(
                        fields,
                        BridgeSupport.ColorRamp(payload),
                        Math.Clamp(BridgeSupport.Double(payload, "dot_size", 2), 0.1, 50),
                        Math.Max(BridgeSupport.Double(payload, "dot_value", 1), double.Epsilon),
                        BridgeSupport.String(payload, "symbol_label", "Dot")!,
                        BridgeSupport.String(payload, "unit_label", "units")!);
                    var backgroundConfig = payload.TryGetProperty("background_symbol", out var backgroundElement)
                        && backgroundElement.ValueKind == JsonValueKind.Object
                        ? backgroundElement
                        : default;
                    if (backgroundConfig.ValueKind == JsonValueKind.Object)
                    {
                        dotDensity.BackgroundSymbol = BridgeSupport.FeatureSymbol(layer, backgroundConfig).MakeSymbolReference();
                    }
                    var expressions = BridgeSupport.Strings(payload, "arcade_expressions");
                    if (expressions.Length > 0)
                    {
                        dotDensity.ArcadeExpressions = expressions.ToList();
                    }
                    definition = dotDensity;
                    break;
                }

                case "pie":
                case "pie_chart":
                {
                    var fields = RequiredRendererFields(payload, rendererType);
                    definition = new PieChartRendererDefinition
                    {
                        ChartFields = fields,
                        ColorRamp = BridgeSupport.ColorRamp(payload),
                        Orientation = EnumValue(payload, "orientation", PieChartOrientation.Clockwise),
                        SizeOption = EnumValue(payload, "size_option", PieChartSizeOptions.Fixed),
                        FieldName = BridgeSupport.String(payload, "field_name", BridgeSupport.String(payload, "field")),
                        FixedSize = Math.Clamp(BridgeSupport.Double(payload, "fixed_size", 12), 0.1, 200),
                        ShowOutline = BridgeSupport.Bool(payload, "show_outline", true),
                        DisplayIn3D = BridgeSupport.Bool(payload, "display_in_3d"),
                    };
                    break;
                }

                case "bar":
                case "bar_chart":
                {
                    definition = new BarChartRendererDefinition
                    {
                        ChartFields = RequiredRendererFields(payload, rendererType),
                        ColorRamp = BridgeSupport.ColorRamp(payload),
                        Orientation = EnumValue(payload, "orientation", ChartOrientation.Vertical),
                        BarSpacing = Math.Max(0, BridgeSupport.Double(payload, "bar_spacing", 1)),
                        BarWidth = Math.Clamp(BridgeSupport.Double(payload, "bar_width", 5), 0.1, 100),
                        MaximumBarLength = Math.Clamp(BridgeSupport.Double(payload, "maximum_bar_length", 20), 0.1, 400),
                        ShowAxes = BridgeSupport.Bool(payload, "show_axes", true),
                        DisplayIn3D = BridgeSupport.Bool(payload, "display_in_3d"),
                    };
                    break;
                }

                case "stacked":
                case "stacked_chart":
                {
                    definition = new StackedChartRendererDefinition
                    {
                        ChartFields = RequiredRendererFields(payload, rendererType),
                        ColorRamp = BridgeSupport.ColorRamp(payload),
                        Orientation = EnumValue(payload, "orientation", ChartOrientation.Vertical),
                        SizeOption = EnumValue(payload, "size_option", StackChartSizeOptions.Fixed),
                        StackLength = Math.Clamp(BridgeSupport.Double(payload, "stack_length", 20), 0.1, 400),
                        StackWidth = Math.Clamp(BridgeSupport.Double(payload, "stack_width", 6), 0.1, 100),
                        ShowOutline = BridgeSupport.Bool(payload, "show_outline", true),
                        DisplayIn3D = BridgeSupport.Bool(payload, "display_in_3d"),
                    };
                    break;
                }

                default:
                    throw new ArgumentException($"Unsupported vector renderer: {rendererType}");
            }

            if (!layer.CanCreateRenderer(definition))
            {
                throw new InvalidOperationException(
                    $"Layer '{layer.Name}' does not support the requested renderer: {rendererType}");
            }
            var renderer = layer.CreateRenderer(definition);
            layer.SetRenderer(renderer);
            if (payload.TryGetProperty("transparency", out var transparency)
                && transparency.ValueKind == JsonValueKind.Number)
            {
                layer.SetTransparency(Math.Clamp(transparency.GetDouble(), 0, 100));
            }

            return (object)new
            {
                map = map.Name,
                layer = layer.Name,
                renderer = rendererType,
                renderer_class = renderer.GetType().Name,
            };
        });
    }

    private static string RequiredField(JsonElement payload, string rendererType)
    {
        return BridgeSupport.String(payload, "field")
            ?? throw new ArgumentException($"field is required for {rendererType} symbology");
    }

    private static List<string> RendererFields(JsonElement payload, string preferredProperty)
    {
        var values = BridgeSupport.Strings(payload, preferredProperty);
        if (values.Length == 0)
        {
            values = BridgeSupport.Strings(payload, "fields");
        }
        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> RequiredRendererFields(JsonElement payload, string rendererType)
    {
        var fields = RendererFields(payload, "chart_fields");
        if (fields.Count == 0)
        {
            throw new ArgumentException($"chart_fields or fields is required for {rendererType} symbology");
        }
        return fields;
    }

    private static double? OptionalDouble(JsonElement payload, string property)
    {
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number)
                ? number
                : null;
    }

    private static DataNormalizationMethod Normalization(JsonElement payload)
    {
        return EnumValue(payload, "normalization_type", DataNormalizationMethod.Nothing);
    }

    private static T EnumValue<T>(JsonElement payload, string property, T fallback)
        where T : struct, Enum
    {
        var text = BridgeSupport.String(payload, property);
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }
        if (Enum.TryParse<T>(text, true, out var parsed))
        {
            return parsed;
        }
        var compact = text.Replace("_", string.Empty)
            .Replace("-", string.Empty)
            .Replace(" ", string.Empty);
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(
                candidate.ToString().Replace("_", string.Empty),
                compact,
                StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
        throw new ArgumentException(
            $"Unsupported {property}: {text}. Expected one of: {string.Join(", ", Enum.GetNames<T>())}");
    }

    internal static async Task<object> ConfigureVisualVariablesAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var operation = BridgeSupport.String(payload, "operation", "replace")!.ToLowerInvariant();

        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = BridgeSupport.ResolveLayer(map, layerName) as FeatureLayer
                ?? throw new InvalidOperationException($"Feature layer not found: {layerName}");
            var renderer = layer.GetRenderer()
                ?? throw new InvalidOperationException($"Layer has no renderer: {layerName}");
            var existing = VisualVariables(renderer).ToList();

            if (operation == "clear")
            {
                existing.Clear();
            }
            else if (operation == "remove")
            {
                var removeTypes = BridgeSupport.Strings(payload, "remove_types")
                    .Select(item => item.ToLowerInvariant())
                    .ToHashSet();
                if (removeTypes.Count == 0)
                {
                    throw new ArgumentException("remove_types is required for remove operation");
                }
                existing.RemoveAll(item => removeTypes.Contains(VisualVariableType(item)));
            }
            else if (operation is "replace" or "append")
            {
                var requested = BuildVisualVariables(payload, layer);
                if (operation == "replace")
                {
                    existing.Clear();
                }
                else
                {
                    var requestedTypes = requested.Select(VisualVariableType).ToHashSet();
                    existing.RemoveAll(item => requestedTypes.Contains(VisualVariableType(item)));
                }
                existing.AddRange(requested);
            }
            else
            {
                throw new ArgumentException($"Unsupported visual-variable operation: {operation}");
            }

            SetVisualVariables(renderer, existing.ToArray());
            layer.SetRenderer(renderer);
            return (object)new
            {
                map = map.Name,
                layer = layer.Name,
                renderer_class = renderer.GetType().Name,
                operation,
                visual_variables = existing.Select(DescribeVisualVariable).ToArray(),
                source_data_modified = false,
            };
        });
    }

    private static List<CIMVisualVariable> BuildVisualVariables(
        JsonElement payload,
        FeatureLayer layer)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("variables", out var variables)
            || variables.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("variables is required for replace or append operation");
        }
        var result = new List<CIMVisualVariable>();
        var types = new HashSet<string>();
        foreach (var config in variables.EnumerateArray())
        {
            if (config.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Each visual variable must be an object");
            }
            var type = BridgeSupport.String(config, "type")?.ToLowerInvariant()
                ?? throw new ArgumentException("Each visual variable requires type");
            if (!types.Add(type))
            {
                throw new ArgumentException($"Duplicate visual-variable type: {type}");
            }
            var expression = VisualVariableExpression(config);
            var authoring = new CIMVisualVariableAuthoringInfo
            {
                Heading = BridgeSupport.String(config, "heading", type),
                ShowLegend = BridgeSupport.Bool(config, "show_legend", true),
                NumberOfHistogramBins = Math.Clamp(
                    BridgeSupport.Int(config, "histogram_bins", 30), 2, 256),
            };
            switch (type)
            {
                case "size":
                {
                    var minValue = BridgeSupport.Double(config, "min_value", 0);
                    var maxValue = BridgeSupport.Double(config, "max_value", 100);
                    if (maxValue <= minValue)
                    {
                        throw new ArgumentException("size max_value must be greater than min_value");
                    }
                    var minSize = Math.Clamp(BridgeSupport.Double(config, "min_size", 4), 0.1, 400);
                    var maxSize = Math.Max(
                        minSize,
                        Math.Clamp(BridgeSupport.Double(config, "max_size", 24), 0.1, 400));
                    result.Add(new CIMSizeVisualVariable
                    {
                        Expression = expression,
                        ValueExpressionInfo = ExpressionInfo(expression, "Size"),
                        NormalizationField = BridgeSupport.String(config, "normalization_field"),
                        NormalizationType = Normalization(config),
                        VariableType = EnumValue(config, "variable_type", SizeVisualVariableType.Proportional),
                        MinValue = minValue,
                        MaxValue = maxValue,
                        MinSize = minSize,
                        MaxSize = maxSize,
                        DataValues = OptionalStops(config, "data_values"),
                        SizeValues = OptionalStops(config, "size_values"),
                        AuthoringInfo = authoring,
                    });
                    break;
                }
                case "color":
                {
                    var minValue = BridgeSupport.Double(config, "min_value", 0);
                    var maxValue = BridgeSupport.Double(config, "max_value", 100);
                    if (maxValue <= minValue)
                    {
                        throw new ArgumentException("color max_value must be greater than min_value");
                    }
                    result.Add(new CIMColorVisualVariable
                    {
                        Expression = expression,
                        ValueExpressionInfo = ExpressionInfo(expression, "Color"),
                        NormalizationField = BridgeSupport.String(config, "normalization_field"),
                        NormalizationType = Normalization(config),
                        MinValue = minValue,
                        MaxValue = maxValue,
                        ColorRamp = BridgeSupport.ColorRamp(config),
                        PolygonSymbolColorTarget = EnumValue(
                            config,
                            "polygon_color_target",
                            PolygonSymbolColorTarget.Fill),
                        AuthoringInfo = authoring,
                    });
                    break;
                }
                case "transparency":
                {
                    var dataValues = BridgeSupport.Doubles(config, "data_values");
                    var transparencyValues = BridgeSupport.Doubles(config, "transparency_values");
                    if (dataValues.Length < 2 || dataValues.Length != transparencyValues.Length)
                    {
                        throw new ArgumentException(
                            "transparency requires matching data_values and transparency_values arrays with at least two values");
                    }
                    if (transparencyValues.Any(value => value < 0 || value > 100))
                    {
                        throw new ArgumentException("transparency_values must be between 0 and 100");
                    }
                    result.Add(new CIMTransparencyVisualVariable
                    {
                        Field = BridgeSupport.String(config, "field"),
                        ValueExpressionInfo = ExpressionInfo(expression, "Transparency"),
                        NormalizationField = BridgeSupport.String(config, "normalization_field"),
                        NormalizationType = Normalization(config),
                        DataValues = dataValues,
                        TransparencyValues = transparencyValues,
                        AuthoringInfo = authoring,
                    });
                    break;
                }
                case "rotation":
                {
                    if (layer.ShapeType is not esriGeometryType.esriGeometryPoint
                        and not esriGeometryType.esriGeometryMultipoint)
                    {
                        throw new InvalidOperationException(
                            "Rotation visual variables require a point or multipoint layer.");
                    }
                    result.Add(new CIMRotationVisualVariable
                    {
                        VisualVariableInfoZ = new CIMVisualVariableInfo
                        {
                            Expression = expression,
                            ValueExpressionInfo = ExpressionInfo(expression, "Rotation"),
                            VisualVariableInfoType = VisualVariableInfoType.Expression,
                        },
                        RotationTypeZ = EnumValue(
                            config,
                            "rotation_type",
                            SymbolRotationType.Geographic),
                        NormalToSurface = BridgeSupport.Bool(config, "normal_to_surface"),
                        AuthoringInfo = authoring,
                    });
                    break;
                }
                default:
                    throw new ArgumentException($"Unsupported visual-variable type: {type}");
            }
        }
        if (result.Count == 0)
        {
            throw new ArgumentException("variables cannot be empty");
        }
        return result;
    }

    private static string VisualVariableExpression(JsonElement config)
    {
        var expression = BridgeSupport.String(config, "expression");
        if (!string.IsNullOrWhiteSpace(expression))
        {
            return expression;
        }
        var field = BridgeSupport.String(config, "field")
            ?? throw new ArgumentException("Each visual variable requires field or expression");
        return $"$feature['{field.Replace("'", "\\'")}']";
    }

    private static CIMExpressionInfo ExpressionInfo(string expression, string name)
    {
        return new CIMExpressionInfo
        {
            Name = $"Codex {name} visual variable",
            Title = name,
            Expression = expression,
            ReturnType = ExpressionReturnType.Numeric,
        };
    }

    private static double[]? OptionalStops(JsonElement payload, string property)
    {
        var values = BridgeSupport.Doubles(payload, property);
        return values.Length > 0 ? values : null;
    }

    private static CIMVisualVariable[] VisualVariables(CIMRenderer renderer)
    {
        return renderer switch
        {
            CIMSimpleRenderer item => item.VisualVariables ?? Array.Empty<CIMVisualVariable>(),
            CIMUniqueValueRenderer item => item.VisualVariables ?? Array.Empty<CIMVisualVariable>(),
            CIMClassBreaksRenderer item => item.VisualVariables ?? Array.Empty<CIMVisualVariable>(),
            CIMProportionalRenderer item => item.VisualVariables ?? Array.Empty<CIMVisualVariable>(),
            CIMDotDensityRenderer item => item.VisualVariables ?? Array.Empty<CIMVisualVariable>(),
            _ => throw new InvalidOperationException(
                $"Renderer does not expose visual variables: {renderer.GetType().Name}"),
        };
    }

    private static void SetVisualVariables(CIMRenderer renderer, CIMVisualVariable[] variables)
    {
        switch (renderer)
        {
            case CIMSimpleRenderer item:
                item.VisualVariables = variables;
                break;
            case CIMUniqueValueRenderer item:
                item.VisualVariables = variables;
                break;
            case CIMClassBreaksRenderer item:
                item.VisualVariables = variables;
                break;
            case CIMProportionalRenderer item:
                item.VisualVariables = variables;
                break;
            case CIMDotDensityRenderer item:
                item.VisualVariables = variables;
                break;
            default:
                throw new InvalidOperationException(
                    $"Renderer does not expose visual variables: {renderer.GetType().Name}");
        }
    }

    private static string VisualVariableType(CIMVisualVariable variable)
    {
        return variable switch
        {
            CIMSizeVisualVariable => "size",
            CIMColorVisualVariable => "color",
            CIMTransparencyVisualVariable => "transparency",
            CIMRotationVisualVariable => "rotation",
            _ => variable.GetType().Name,
        };
    }

    private static object DescribeVisualVariable(CIMVisualVariable variable)
    {
        return variable switch
        {
            CIMSizeVisualVariable item => new
            {
                type = "size",
                expression = item.Expression,
                min_value = item.MinValue,
                max_value = item.MaxValue,
                min_size = item.MinSize,
                max_size = item.MaxSize,
            },
            CIMColorVisualVariable item => new
            {
                type = "color",
                expression = item.Expression,
                min_value = item.MinValue,
                max_value = item.MaxValue,
                color_ramp = DescribeColorRamp(item.ColorRamp),
            },
            CIMTransparencyVisualVariable item => new
            {
                type = "transparency",
                field = item.Field,
                data_values = item.DataValues,
                transparency_values = item.TransparencyValues,
            },
            CIMRotationVisualVariable item => new
            {
                type = "rotation",
                expression = item.VisualVariableInfoZ?.Expression,
                rotation_type = item.RotationTypeZ.ToString(),
            },
            _ => new { type = variable.GetType().Name },
        };
    }

    internal static async Task<object> ConfigureRasterRenderingRuleAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var operation = BridgeSupport.String(payload, "operation", "inspect")!.ToLowerInvariant();

        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = BridgeSupport.ResolveLayer(map, layerName) as BasicRasterLayer
                ?? throw new InvalidOperationException($"Raster layer not found: {layerName}");
            var definition = layer.GetDefinition() as CIMRasterLayer
                ?? throw new InvalidOperationException(
                    $"Raster layer CIM is not supported: {layer.GetType().Name}");

            if (operation == "remove")
            {
                definition.RenderingRule = null;
                layer.SetDefinition(definition);
            }
            else if (operation == "set")
            {
                if (!BridgeSupport.Bool(payload, "confirm_replace_definition"))
                    throw new ArgumentException("confirm_replace_definition=true is required for set operation");
                var rasterLayer = layer as RasterLayer
                    ?? throw new InvalidOperationException(
                        "Rendering-rule replacement is supported only for RasterLayer");
                var cimJson = BridgeSupport.String(payload, "cim_json")
                    ?? throw new ArgumentException(
                        "cim_json from a complete, previously inspected CIMRenderingRule is required for set operation");
                if (cimJson.Length > 1024 * 1024)
                    throw new ArgumentException("cim_json is limited to 1 MB");
                var replacement = CIMRenderingRule.FromJson(
                    cimJson, new JsonDeserializationSettings());
                if (replacement is null
                    || string.IsNullOrWhiteSpace(replacement.Name)
                    || string.IsNullOrWhiteSpace(replacement.Definition))
                {
                    throw new ArgumentException(
                        "cim_json must contain a complete CIMRenderingRule with non-empty name and definition");
                }
                rasterLayer.SetRenderingRule(replacement);
                definition = rasterLayer.GetDefinition() as CIMRasterLayer
                    ?? throw new InvalidOperationException("Raster layer definition was unavailable after replacement");
            }
            else if (operation != "inspect")
            {
                throw new ArgumentException($"Unsupported rendering-rule operation: {operation}");
            }

            return (object)new
            {
                map = map.Name,
                layer = layer.Name,
                operation,
                rendering_rule = DescribeRenderingRule(definition.RenderingRule),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> ConfigureMultidimensionalRasterAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var operation = BridgeSupport.String(payload, "operation", "inspect")!.ToLowerInvariant();

        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = BridgeSupport.ResolveLayer(map, layerName) as BasicRasterLayer
                ?? throw new InvalidOperationException($"Raster layer not found: {layerName}");
            var definition = layer.GetDefinition() as CIMRasterLayer
                ?? throw new InvalidOperationException(
                    $"Raster layer CIM is not supported: {layer.GetType().Name}");

            if (operation == "clear")
            {
                definition.ActiveSlice = null;
                definition.ActiveVariables = Array.Empty<string>();
                layer.SetDefinition(definition);
            }
            else if (operation == "set")
            {
                if (definition.MultidimensionalExtent is null
                    && definition.ActiveSlice is null
                    && (definition.ActiveVariables is null || definition.ActiveVariables.Length == 0))
                {
                    throw new InvalidOperationException(
                        "Raster layer is not multidimensional; an active variable/slice cannot be set safely");
                }
                var variableName = BridgeSupport.String(payload, "variable_name")
                    ?? throw new ArgumentException("variable_name is required for set operation");
                var slice = new CIMRasterMultidimensionalDisplayDefinition
                {
                    VariableName = variableName,
                    RangeDimensionName = BridgeSupport.String(payload, "range_dimension_name"),
                };
                var rangeMin = OptionalDouble(payload, "range_min");
                var rangeMax = OptionalDouble(payload, "range_max");
                if (rangeMin.HasValue || rangeMax.HasValue)
                {
                    if (!rangeMin.HasValue || !rangeMax.HasValue || rangeMax < rangeMin)
                    {
                        throw new ArgumentException(
                            "range_min and range_max must both be provided and range_max must be >= range_min");
                    }
                    slice.HasRangeDimension = true;
                    slice.RangeDimensionValue = new CIMRange
                    {
                        Min = rangeMin.Value,
                        Max = rangeMax.Value,
                    };
                }
                slice.AdditionalDimensionValues = AdditionalDimensionValues(payload);
                slice.TimeValue = TimeValue(payload);
                definition.ActiveSlice = slice;
                definition.ActiveVariables = new[] { variableName };
                layer.SetDefinition(definition);
            }
            else if (operation != "inspect")
            {
                throw new ArgumentException($"Unsupported multidimensional operation: {operation}");
            }

            return (object)new
            {
                map = map.Name,
                layer = layer.Name,
                operation,
                active_variables = definition.ActiveVariables,
                active_slice = DescribeActiveSlice(definition.ActiveSlice),
                has_multidimensional_extent = definition.MultidimensionalExtent is not null,
                source_data_modified = false,
            };
        });
    }

    private static object? DescribeRenderingRule(CIMRenderingRule? rule)
    {
        return rule is null
            ? null
            : new
            {
                name = rule.Name,
                description = rule.Description,
                variable_name = rule.VariableName,
                definition = rule.Definition,
                arguments = rule.Arguments,
            };
    }

    private static CIMRangeDimensionValue[]? AdditionalDimensionValues(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("additional_dimensions", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var result = new List<CIMRangeDimensionValue>();
        foreach (var item in values.EnumerateArray())
        {
            var name = BridgeSupport.String(item, "name")
                ?? throw new ArgumentException("Each additional dimension requires name");
            var minimum = OptionalDouble(item, "min")
                ?? throw new ArgumentException($"Additional dimension {name} requires min");
            var maximum = OptionalDouble(item, "max")
                ?? throw new ArgumentException($"Additional dimension {name} requires max");
            if (maximum < minimum)
            {
                throw new ArgumentException(
                    $"Additional dimension {name} max must be >= min");
            }
            result.Add(new CIMRangeDimensionValue
            {
                RangeDimensionName = name,
                RangeDimensionValue = new CIMRange { Min = minimum, Max = maximum },
            });
        }
        return result.ToArray();
    }

    private static TimeExtent? TimeValue(JsonElement payload)
    {
        var startText = BridgeSupport.String(payload, "time_start");
        var endText = BridgeSupport.String(payload, "time_end");
        if (string.IsNullOrWhiteSpace(startText) && string.IsNullOrWhiteSpace(endText))
        {
            return null;
        }
        if (!DateTime.TryParse(startText, out var start)
            || !DateTime.TryParse(endText, out var end)
            || end < start)
        {
            throw new ArgumentException(
                "time_start and time_end must be valid dates and time_end must be >= time_start");
        }
        return new TimeExtent
        {
            StartTime = start,
            EndTime = end,
            StartTimeSpecified = true,
            EndTimeSpecified = true,
        };
    }

    private static object? DescribeActiveSlice(
        CIMRasterMultidimensionalDisplayDefinition? slice)
    {
        return slice is null
            ? null
            : new
            {
                variable_name = slice.VariableName,
                has_range_dimension = slice.HasRangeDimension,
                range_dimension_name = slice.RangeDimensionName,
                range_min = slice.RangeDimensionValue?.Min,
                range_max = slice.RangeDimensionValue?.Max,
                time_start = slice.TimeValue?.StartTime,
                time_end = slice.TimeValue?.EndTime,
                additional_dimensions = slice.AdditionalDimensionValues?.Select(item => new
                {
                    name = item.RangeDimensionName,
                    min = item.RangeDimensionValue?.Min,
                    max = item.RangeDimensionValue?.Max,
                }).ToArray(),
            };
    }

    internal static async Task<object> ApplyRasterAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layerName = BridgeSupport.String(payload, "layer_name")
            ?? throw new ArgumentException("layer_name is required");
        var requestedMap = BridgeSupport.String(payload, "map_name");
        var colorizerType = BridgeSupport.String(payload, "colorizer", "stretch")!.ToLowerInvariant();

        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(requestedMap);
            var layer = BridgeSupport.ResolveLayer(map, layerName) as BasicRasterLayer
                ?? throw new InvalidOperationException($"Raster layer not found: {layerName}");
            var ramp = BridgeSupport.ColorRamp(payload);
            RasterColorizerDefinition definition;

            switch (colorizerType)
            {
                case "stretch":
                    var stretch = new StretchColorizerDefinition(
                        BridgeSupport.Int(payload, "band_index", 0),
                        BridgeSupport.Stretch(BridgeSupport.String(payload, "stretch_type", "PercentMinimumMaximum")),
                        BridgeSupport.Double(payload, "gamma", 1.0),
                        ramp)
                    {
                        MinPercent = BridgeSupport.Double(payload, "min_percent", 2.0),
                        MaxPercent = BridgeSupport.Double(payload, "max_percent", 2.0),
                        StandardDeviationsParam = BridgeSupport.Double(payload, "standard_deviations", 2.0),
                        DisplayBackgroundValue = BridgeSupport.Bool(payload, "display_background_value"),
                        BackgroundValue = OptionalDouble(payload, "background_value"),
                        BackgroundColor = BridgeSupport.Color(payload, "background_color", ColorFactory.Instance.CreateRGBColor(255, 255, 255, 0)),
                        NoDataColor = BridgeSupport.Color(payload, "nodata_color", ColorFactory.Instance.CreateRGBColor(255, 255, 255, 0)),
                    };
                    definition = stretch;
                    break;

                case "classify":
                case "classified":
                    definition = new ClassifyColorizerDefinition(
                        BridgeSupport.String(payload, "field", "Value")!,
                        Math.Clamp(BridgeSupport.Int(payload, "class_count", 7), 2, 32),
                        BridgeSupport.Classification(BridgeSupport.String(payload, "classification", "NaturalBreaks")),
                        ramp)
                    {
                        NoDataColor = BridgeSupport.Color(payload, "nodata_color", ColorFactory.Instance.CreateRGBColor(255, 255, 255, 0)),
                    };
                    break;

                case "discrete":
                    definition = new DiscreteColorizerDefinition(
                        Math.Clamp(BridgeSupport.Int(payload, "color_count", 9), 2, 64),
                        ramp)
                    {
                        NoDataColor = BridgeSupport.Color(payload, "nodata_color", ColorFactory.Instance.CreateRGBColor(255, 255, 255, 0)),
                    };
                    break;

                case "shaded_relief":
                case "relief":
                    definition = new ShadedReliefColorizerDefinition(
                        BridgeSupport.Double(payload, "z_factor", 1.0),
                        BridgeSupport.Double(payload, "altitude", 45),
                        BridgeSupport.Double(payload, "azimuth", 315),
                        ramp,
                        EnumValue(payload, "hillshade_type", ColorizerHillshadeType.Traditional),
                        EnumValue(payload, "scaling_type", ColorizerScalingType.None))
                    {
                        RemoveEdgeEffect = BridgeSupport.Bool(payload, "remove_edge_effect", true),
                        UseMapIlluminationProperties = BridgeSupport.Bool(payload, "use_map_illumination"),
                        PixelSizeFactor = BridgeSupport.Double(payload, "pixel_size_factor", 1.0),
                        PixelSizePower = BridgeSupport.Double(payload, "pixel_size_power", 1.0),
                    };
                    break;

                case "rgb":
                case "rgb_composite":
                    definition = new RGBColorizerDefinition(
                        Math.Max(0, BridgeSupport.Int(payload, "red_band_index", 0)),
                        Math.Max(0, BridgeSupport.Int(payload, "green_band_index", 1)),
                        Math.Max(0, BridgeSupport.Int(payload, "blue_band_index", 2)),
                        BridgeSupport.Stretch(BridgeSupport.String(payload, "stretch_type", "PercentMinimumMaximum")),
                        Math.Max(0.01, BridgeSupport.Double(payload, "gamma_r", 1.0)),
                        Math.Max(0.01, BridgeSupport.Double(payload, "gamma_g", 1.0)),
                        Math.Max(0.01, BridgeSupport.Double(payload, "gamma_b", 1.0)))
                    {
                        MinPercent = BridgeSupport.Double(payload, "min_percent", 2.0),
                        MaxPercent = BridgeSupport.Double(payload, "max_percent", 2.0),
                        StandardDeviationsParam = BridgeSupport.Double(payload, "standard_deviations", 2.0),
                        DisplayBackgroundValue = BridgeSupport.Bool(payload, "display_background_value"),
                        BackgroundValueRed = OptionalDouble(payload, "background_value_red"),
                        BackgroundValueGreen = OptionalDouble(payload, "background_value_green"),
                        BackgroundValueBlue = OptionalDouble(payload, "background_value_blue"),
                        BackgroundColor = BridgeSupport.Color(payload, "background_color", ColorFactory.Instance.CreateRGBColor(255, 255, 255, 0)),
                        NoDataColor = BridgeSupport.Color(payload, "nodata_color", ColorFactory.Instance.CreateRGBColor(255, 255, 255, 0)),
                    };
                    break;

                case "unique":
                case "unique_value":
                    definition = new UniqueValueColorizerDefinition(
                        BridgeSupport.String(payload, "field", "Value")!,
                        ramp)
                    {
                        NoDataColor = BridgeSupport.Color(payload, "nodata_color", ColorFactory.Instance.CreateRGBColor(255, 255, 255, 0)),
                    };
                    break;

                case "colormap":
                case "color_map":
                {
                    var colormapFile = BridgeSupport.String(payload, "colormap_file");
                    if (!string.IsNullOrWhiteSpace(colormapFile))
                    {
                        definition = new ColormapColorizerDefinition(colormapFile)
                        {
                            NoDataColor = BridgeSupport.Color(payload, "nodata_color", ColorFactory.Instance.CreateRGBColor(255, 255, 255, 0)),
                        };
                        break;
                    }
                    var entries = ColormapEntries(payload);
                    definition = new ColormapColorizerDefinition(
                        entries.Select(item => item.Color).ToList(),
                        entries.Select(item => item.Value).ToList(),
                        entries.Select(item => item.Label).ToList())
                    {
                        NoDataColor = BridgeSupport.Color(payload, "nodata_color", ColorFactory.Instance.CreateRGBColor(255, 255, 255, 0)),
                    };
                    break;
                }

                case "vector_field":
                case "vector":
                    definition = new VectorFieldColorizerDefinition(
                        Math.Max(0, BridgeSupport.Int(payload, "magnitude_band_index", 0)),
                        Math.Max(0, BridgeSupport.Int(payload, "direction_band_index", 1)),
                        BridgeSupport.Bool(payload, "is_uv_components"),
                        EnumValue(payload, "symbolization_type", SymbolizationType.SingleArrow));
                    break;

                default:
                    throw new ArgumentException($"Unsupported raster colorizer: {colorizerType}");
            }

            if (!layer.CanCreateColorizer(definition))
            {
                throw new InvalidOperationException(
                    $"The raster does not support the requested colorizer: {colorizerType}");
            }
            var colorizer = layer.CreateColorizer(definition);

            colorizer.Brightness = Math.Clamp(BridgeSupport.Int(payload, "brightness", 0), -100, 100);
            colorizer.Contrast = Math.Clamp(BridgeSupport.Int(payload, "contrast", 0), -100, 100);
            colorizer.ResamplingType = EnumValue(
                payload,
                "resampling_type",
                RasterResamplingType.NearestNeighbor);

            if (colorizer is CIMRasterStretchColorizer stretchColorizer)
            {
                if (payload.TryGetProperty("minimum", out var minimum)
                    && minimum.ValueKind == JsonValueKind.Number
                    && payload.TryGetProperty("maximum", out var maximum)
                    && maximum.ValueKind == JsonValueKind.Number)
                {
                    stretchColorizer.UseCustomStretchMinMax = true;
                    stretchColorizer.CustomStretchMin = minimum.GetDouble();
                    stretchColorizer.CustomStretchMax = maximum.GetDouble();
                }
                stretchColorizer.Heading = BridgeSupport.String(payload, "heading", "Value");
            }
            if (colorizer is CIMRasterClassifyColorizer classifyColorizer)
            {
                classifyColorizer.Heading = BridgeSupport.String(payload, "heading", "Value");
                var labels = BridgeSupport.Strings(payload, "class_labels");
                if (labels.Length > 0 && classifyColorizer.ClassBreaks is not null)
                {
                    for (var index = 0; index < Math.Min(labels.Length, classifyColorizer.ClassBreaks.Length); index++)
                    {
                        classifyColorizer.ClassBreaks[index].Label = labels[index];
                    }
                }
            }
            if (colorizer is CIMRasterRGBColorizer rgbColorizer)
            {
                var minimums = BridgeSupport.Doubles(payload, "minimums");
                var maximums = BridgeSupport.Doubles(payload, "maximums");
                if (minimums.Length >= 3 && maximums.Length >= 3)
                {
                    rgbColorizer.UseCustomStretchMinMax = true;
                    rgbColorizer.CustomStretchMinRed = minimums[0];
                    rgbColorizer.CustomStretchMinGreen = minimums[1];
                    rgbColorizer.CustomStretchMinBlue = minimums[2];
                    rgbColorizer.CustomStretchMaxRed = maximums[0];
                    rgbColorizer.CustomStretchMaxGreen = maximums[1];
                    rgbColorizer.CustomStretchMaxBlue = maximums[2];
                }
                rgbColorizer.Heading = BridgeSupport.String(payload, "heading", "RGB")!;
            }

            layer.SetColorizer(colorizer);
            var refreshedColorbars = LayoutActions.RefreshLinkedRasterColorbars(map, layer);
            if (payload.TryGetProperty("transparency", out var transparency)
                && transparency.ValueKind == JsonValueKind.Number)
            {
                layer.SetTransparency(Math.Clamp(transparency.GetDouble(), 0, 100));
            }

            return (object)new
            {
                map = map.Name,
                layer = layer.Name,
                colorizer = colorizerType,
                colorizer_class = colorizer.GetType().Name,
                refreshed_linked_colorbars = refreshedColorbars,
            };
        });
    }

    private static List<(int Value, CIMColor Color, string Label)> ColormapEntries(
        JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("colormap_entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException(
                "colormap_entries or colormap_file is required for colormap symbology");
        }
        var result = new List<(int Value, CIMColor Color, string Label)>();
        var seen = new HashSet<int>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("value", out var value)
                || !value.TryGetInt32(out var number)
                || !entry.TryGetProperty("color", out var color))
            {
                throw new ArgumentException(
                    "Each colormap entry requires an integer value and a color");
            }
            if (!seen.Add(number))
            {
                throw new ArgumentException($"Duplicate colormap value: {number}");
            }
            result.Add((
                number,
                BridgeSupport.Color(color),
                BridgeSupport.String(entry, "label", number.ToString())!));
        }
        if (result.Count == 0)
        {
            throw new ArgumentException("colormap_entries cannot be empty");
        }
        return result;
    }
}
