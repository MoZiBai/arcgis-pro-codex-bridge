using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class CartographyActions
{
    internal static async Task<object> ListStyleItemsAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var itemType = BridgeSupport.String(payload, "item_type", "grid")!.ToLowerInvariant();
        var search = BridgeSupport.String(payload, "search", string.Empty)!;
        var requestedStyle = BridgeSupport.String(payload, "style_name");
        var maximum = Math.Clamp(BridgeSupport.Int(payload, "max_items", 200), 1, 5000);
        return await QueuedTask.Run<object>(() =>
        {
            var styles = Project.Current.GetItems<StyleProjectItem>()
                .Where(style => string.IsNullOrWhiteSpace(requestedStyle)
                    || string.Equals(style.Name, requestedStyle, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var items = styles.SelectMany(style => SearchStyle(style, itemType, search)
                    .Select(item => new
                    {
                        style = style.Name,
                        style_path = style.Path,
                        name = item.Name,
                        category = item.Category,
                        tags = item.Tags,
                        key = item.Key,
                        item_type = item.ItemType.ToString(),
                    }))
                .Take(maximum)
                .ToArray();
            return (object)new
            {
                item_type = itemType,
                search,
                style_name = requestedStyle,
                returned = items.Length,
                truncated = items.Length >= maximum,
                items,
            };
        });
    }

    internal static async Task<object> ListMapGridsAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var includeCim = BridgeSupport.Bool(payload, "include_cim", false);
        return await QueuedTask.Run<object>(() =>
        {
            var frame = ResolveMapFrame(layoutName, BridgeSupport.String(payload, "map_frame_name"));
            var grids = frame.GetGrids() ?? Array.Empty<CIMMapGrid>();
            return (object)new
            {
                layout = layoutName,
                map_frame = frame.Name,
                grids = grids.Select((grid, index) => new
                {
                    index,
                    name = grid.Name,
                    grid_type = grid.GetType().Name,
                    visible = grid.IsVisible,
                    minimum_scale = grid.MinScale,
                    maximum_scale = grid.MaxScale,
                    cim_json = includeCim ? grid.ToJson() : null,
                }).ToArray(),
            };
        });
    }

    internal static async Task<object> AddMapGridAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var styleItemName = BridgeSupport.String(payload, "style_item_name");
        var search = BridgeSupport.String(payload, "search", styleItemName ?? string.Empty)!;
        return await QueuedTask.Run<object>(() =>
        {
            var frame = ResolveMapFrame(layoutName, BridgeSupport.String(payload, "map_frame_name"));
            var styleItem = ResolveStyleItem<GridStyleItem>(
                "grid",
                BridgeSupport.String(payload, "style_name"),
                styleItemName,
                search);
            var before = frame.GetGrids()?.Length ?? 0;
            frame.AddGrid(styleItem);
            var grids = frame.GetGrids() ?? Array.Empty<CIMMapGrid>();
            if (grids.Length <= before)
            {
                throw new InvalidOperationException("ArcGIS Pro did not add the requested map grid.");
            }
            var added = grids.Last();
            var requestedName = BridgeSupport.String(payload, "grid_name");
            if (!string.IsNullOrWhiteSpace(requestedName))
            {
                added.Name = requestedName;
                frame.SetGrids(grids);
            }
            return (object)new
            {
                layout = layoutName,
                map_frame = frame.Name,
                style = styleItem.StylePath,
                style_item = styleItem.Name,
                grid_name = added.Name,
                grid_type = added.GetType().Name,
                grid_count = grids.Length,
            };
        });
    }

    internal static async Task<object> SetMapGridCimAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var gridName = BridgeSupport.String(payload, "grid_name")
            ?? throw new ArgumentException("grid_name is required");
        var gridType = BridgeSupport.String(payload, "grid_type")
            ?? throw new ArgumentException("grid_type is required");
        var cimJson = BridgeSupport.String(payload, "cim_json")
            ?? throw new ArgumentException("cim_json is required");
        if (!BridgeSupport.Bool(payload, "confirm_replace_definition", false))
        {
            throw new ArgumentException("confirm_replace_definition=true is required");
        }
        return await QueuedTask.Run<object>(() =>
        {
            var frame = ResolveMapFrame(layoutName, BridgeSupport.String(payload, "map_frame_name"));
            var grids = frame.GetGrids()?.ToArray() ?? Array.Empty<CIMMapGrid>();
            var index = Array.FindIndex(grids, grid => string.Equals(
                grid.Name, gridName, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new InvalidOperationException($"Map grid not found: {gridName}");
            }
            var replacement = ParseGrid(gridType, cimJson);
            grids[index] = replacement;
            frame.SetGrids(grids);
            return (object)new
            {
                layout = layoutName,
                map_frame = frame.Name,
                old_grid_name = gridName,
                grid_name = replacement.Name,
                grid_type = replacement.GetType().Name,
                definition_replaced = true,
            };
        });
    }

    internal static async Task<object> ConfigureMapGridAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var gridName = BridgeSupport.String(payload, "grid_name")
            ?? throw new ArgumentException("grid_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            var frame = ResolveMapFrame(layoutName, BridgeSupport.String(payload, "map_frame_name"));
            var grids = frame.GetGrids()?.ToArray() ?? Array.Empty<CIMMapGrid>();
            var index = Array.FindIndex(grids, grid => string.Equals(
                grid.Name, gridName, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new InvalidOperationException($"Map grid not found: {gridName}");
            }
            CIMMapGrid replacement = grids[index] switch
            {
                CIMGraticule graticule => graticule.Clone() as CIMGraticule ?? graticule,
                CIMMeasuredGrid measured => measured.Clone() as CIMMeasuredGrid ?? measured,
                _ => throw new InvalidOperationException(
                    "Typed grid configuration currently supports graticules and measured grids."),
            };
            var newName = BridgeSupport.String(payload, "new_name");
            if (!string.IsNullOrWhiteSpace(newName))
            {
                replacement.Name = newName;
            }
            if (payload.TryGetProperty("visible", out _))
                replacement.IsVisible = BridgeSupport.Bool(payload, "visible", true);
            if (payload.TryGetProperty("minimum_scale", out _))
                replacement.MinScale = Math.Max(0, BridgeSupport.Double(payload, "minimum_scale"));
            if (payload.TryGetProperty("maximum_scale", out _))
                replacement.MaxScale = Math.Max(0, BridgeSupport.Double(payload, "maximum_scale"));

            var lines = replacement switch
            {
                CIMGraticule graticule => graticule.GridLines ?? Array.Empty<CIMGridLine>(),
                CIMMeasuredGrid measured => measured.GridLines ?? Array.Empty<CIMGridLine>(),
                _ => Array.Empty<CIMGridLine>(),
            };
            foreach (var line in lines)
            {
                line.Pattern ??= new CIMGridPattern();
                var interval = line.GridLineOrientation == GridLineOrientation.NorthSouth
                    ? BridgeSupport.Double(payload, "north_south_interval", 0)
                    : BridgeSupport.Double(payload, "east_west_interval", 0);
                if (interval <= 0)
                {
                    interval = BridgeSupport.Double(payload, "interval", 0);
                }
                if (interval > 0)
                {
                    line.Pattern.Interval = interval;
                }
                if (payload.TryGetProperty("draw_grid_lines", out _)
                    && !BridgeSupport.Bool(payload, "draw_grid_lines", true))
                {
                    line.Symbol = null;
                }
                ConfigureExteriorTick(line.FromTick, payload, "from");
                ConfigureExteriorTick(line.ToTick, payload, "to");
                foreach (var tick in line.InteriorTicks ?? Array.Empty<CIMInteriorTick>())
                {
                    if (payload.TryGetProperty("interior_ticks_visible", out _))
                        tick.IsVisible = BridgeSupport.Bool(payload, "interior_ticks_visible", false);
                    if (payload.TryGetProperty("interior_tick_length", out _))
                        tick.Length = Math.Max(0, BridgeSupport.Double(payload, "interior_tick_length"));
                    if (payload.TryGetProperty("interior_tick_offset", out _))
                        tick.Offset = BridgeSupport.Double(payload, "interior_tick_offset");
                }
            }
            if (replacement is CIMGraticule updatedGraticule)
                updatedGraticule.GridLines = lines;
            else if (replacement is CIMMeasuredGrid updatedMeasured)
                updatedMeasured.GridLines = lines;
            grids[index] = replacement;
            frame.SetGrids(grids);
            return (object)new
            {
                layout = layoutName,
                map_frame = frame.Name,
                old_grid_name = gridName,
                grid_name = replacement.Name,
                grid_type = replacement.GetType().Name,
                visible = replacement.IsVisible,
                grid_lines = lines.Select(line => new
                {
                    name = line.Name,
                    orientation = line.GridLineOrientation.ToString(),
                    interval = line.Pattern?.Interval,
                    draws_grid_line = line.Symbol is not null,
                    from_tick_visible = line.FromTick?.IsVisible,
                    to_tick_visible = line.ToTick?.IsVisible,
                    from_labels_present = line.FromTick?.GridEndpoint?.GridLabelTemplate is not null,
                    to_labels_present = line.ToTick?.GridEndpoint?.GridLabelTemplate is not null,
                }).ToArray(),
                source_data_modified = false,
            };
        });
    }

    internal static async Task<object> RemoveMapGridsAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var names = BridgeSupport.Strings(payload, "grid_names");
        if (names.Length == 0)
        {
            throw new ArgumentException("grid_names must contain at least one grid name");
        }
        if (!BridgeSupport.Bool(payload, "confirm_remove", false))
        {
            throw new ArgumentException("confirm_remove=true is required to remove map grids");
        }
        return await QueuedTask.Run<object>(() =>
        {
            var frame = ResolveMapFrame(layoutName, BridgeSupport.String(payload, "map_frame_name"));
            var grids = frame.GetGrids() ?? Array.Empty<CIMMapGrid>();
            var remaining = grids.Where(grid => !names.Contains(
                grid.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
            frame.SetGrids(remaining);
            return (object)new
            {
                layout = layoutName,
                map_frame = frame.Name,
                requested = names,
                removed = grids.Length - remaining.Length,
                remaining = remaining.Select(grid => grid.Name).ToArray(),
            };
        });
    }

    internal static async Task<object> ListMapSurroundsAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var includeCim = BridgeSupport.Bool(payload, "include_cim", false);
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var surrounds = layout.GetElementsAsFlattenedList().OfType<MapSurround>()
                .Select(surround => new
                {
                    name = surround.Name,
                    surround_type = surround.GetType().Name,
                    x = surround.GetX(),
                    y = surround.GetY(),
                    width = surround.GetWidth(),
                    height = surround.GetHeight(),
                    rotation = surround.GetRotation(),
                    visible = surround.IsVisible,
                    locked = surround.IsLocked,
                    cim_json = includeCim ? surround.GetDefinition().ToJson() : null,
                }).ToArray();
            return (object)new { layout = layoutName, surrounds };
        });
    }

    internal static async Task<object> AddMapSurroundAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var surroundType = BridgeSupport.String(payload, "surround_type")
            ?? throw new ArgumentException("surround_type is required");
        var elementName = BridgeSupport.String(payload, "element_name", surroundType)!;
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var frame = ResolveMapFrame(layoutName, BridgeSupport.String(payload, "map_frame_name"));
            var info = SurroundInfo(surroundType, frame.Name);
            var bounds = BridgeSupport.Bounds(payload, "bounds", 1, 1, 5, 4);
            var surround = ElementFactory.Instance.CreateMapSurroundElement(
                layout, bounds, info, elementName, false, null);
            ApplySurroundParameters(surround, payload);
            ApplyOptionalStyle(surround, surroundType, payload);
            return (object)new
            {
                layout = layoutName,
                map_frame = frame.Name,
                element = surround.Name,
                surround_type = surround.GetType().Name,
            };
        });
    }

    internal static async Task<object> ConfigureMapSurroundAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var elementName = BridgeSupport.String(payload, "element_name")
            ?? throw new ArgumentException("element_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var surround = layout.FindElement(elementName) as MapSurround
                ?? throw new InvalidOperationException($"Map surround not found: {elementName}");
            var frameName = BridgeSupport.String(payload, "map_frame_name");
            if (!string.IsNullOrWhiteSpace(frameName))
            {
                surround.SetMapFrame(ResolveMapFrame(layoutName, frameName));
            }
            ApplyElementTransform(surround, payload);
            ApplySurroundParameters(surround, payload);
            ApplyOptionalStyle(surround, surround.GetType().Name, payload);
            return (object)new
            {
                layout = layoutName,
                element = surround.Name,
                surround_type = surround.GetType().Name,
                x = surround.GetX(),
                y = surround.GetY(),
                width = surround.GetWidth(),
                height = surround.GetHeight(),
                visible = surround.IsVisible,
            };
        });
    }

    internal static async Task<object> SetMapSurroundCimAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var layoutName = BridgeSupport.String(payload, "layout_name")
            ?? throw new ArgumentException("layout_name is required");
        var elementName = BridgeSupport.String(payload, "element_name")
            ?? throw new ArgumentException("element_name is required");
        var surroundType = BridgeSupport.String(payload, "surround_type")
            ?? throw new ArgumentException("surround_type is required");
        var cimJson = BridgeSupport.String(payload, "cim_json")
            ?? throw new ArgumentException("cim_json is required");
        if (!BridgeSupport.Bool(payload, "confirm_replace_definition", false))
        {
            throw new ArgumentException("confirm_replace_definition=true is required");
        }
        return await QueuedTask.Run<object>(() =>
        {
            var layout = BridgeSupport.ResolveLayout(layoutName);
            var surround = layout.FindElement(elementName) as MapSurround
                ?? throw new InvalidOperationException($"Map surround not found: {elementName}");
            CIMElement definition = surroundType.ToLowerInvariant() switch
            {
                "legend" => CIMLegend.FromJson(cimJson, new JsonDeserializationSettings()),
                "north_arrow" or "northarrow" => (CIMElement)CIMNorthArrow.FromJson(
                    cimJson, new JsonDeserializationSettings()),
                "scale_bar" or "scalebar" => (CIMElement)CIMScaleBar.FromJson(
                    cimJson, new JsonDeserializationSettings()),
                _ => throw new ArgumentException($"Unsupported map surround CIM type: {surroundType}"),
            };
            surround.SetDefinition(definition);
            return (object)new
            {
                layout = layoutName,
                element = surround.Name,
                surround_type = surround.GetType().Name,
                definition_replaced = true,
            };
        });
    }

    private static MapFrame ResolveMapFrame(string layoutName, string? frameName)
    {
        var layout = BridgeSupport.ResolveLayout(layoutName);
        var frames = layout.GetElementsAsFlattenedList().OfType<MapFrame>().ToArray();
        if (!string.IsNullOrWhiteSpace(frameName))
        {
            return frames.FirstOrDefault(frame => string.Equals(
                frame.Name, frameName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Map frame not found: {frameName}");
        }
        return frames.FirstOrDefault()
            ?? throw new InvalidOperationException($"Layout has no map frame: {layoutName}");
    }

    private static IEnumerable<StyleItem> SearchStyle(
        StyleProjectItem style,
        string itemType,
        string search)
    {
        return itemType switch
        {
            "grid" or "map_grid" => StyleHelper.SearchGrids(style, search),
            "legend" => StyleHelper.SearchLegends(style, search),
            "north_arrow" or "northarrow" => StyleHelper.SearchNorthArrows(style, search),
            "scale_bar" or "scalebar" => StyleHelper.SearchScaleBars(style, search),
            "table_frame" or "tableframe" => StyleHelper.SearchTableFrames(style, search),
            "table_frame_field" or "tableframefield" => StyleHelper.SearchTableFrameFields(style, search),
            "standard_label_placement" => StyleHelper.SearchLabelPlacements(
                style, StyleItemType.StandardLabelPlacement, search),
            "maplex_label_placement" => StyleHelper.SearchLabelPlacements(
                style, StyleItemType.MaplexLabelPlacement, search),
            _ => throw new ArgumentException($"Unsupported style item type: {itemType}"),
        };
    }

    private static void ConfigureExteriorTick(
        CIMExteriorTick? tick,
        JsonElement payload,
        string endpoint)
    {
        if (tick is null)
        {
            return;
        }
        var specificVisible = endpoint + "_ticks_visible";
        if (payload.TryGetProperty(specificVisible, out _))
            tick.IsVisible = BridgeSupport.Bool(payload, specificVisible, true);
        else if (payload.TryGetProperty("exterior_ticks_visible", out _))
            tick.IsVisible = BridgeSupport.Bool(payload, "exterior_ticks_visible", true);
        if (payload.TryGetProperty("exterior_tick_length", out _))
            tick.Length = Math.Max(0, BridgeSupport.Double(payload, "exterior_tick_length"));
        if (payload.TryGetProperty("exterior_tick_offset", out _))
            tick.Offset = BridgeSupport.Double(payload, "exterior_tick_offset");
        var labelsVisible = endpoint + "_labels_visible";
        if (payload.TryGetProperty(labelsVisible, out _)
            && !BridgeSupport.Bool(payload, labelsVisible, true)
            && tick.GridEndpoint is not null)
        {
            tick.GridEndpoint.GridLabelTemplate = null;
        }
        else if (payload.TryGetProperty("labels_visible", out _)
            && !BridgeSupport.Bool(payload, "labels_visible", true)
            && tick.GridEndpoint is not null)
        {
            tick.GridEndpoint.GridLabelTemplate = null;
        }
    }

    private static T ResolveStyleItem<T>(
        string itemType,
        string? styleName,
        string? exactName,
        string search)
        where T : StyleItem
    {
        var candidates = Project.Current.GetItems<StyleProjectItem>()
            .Where(style => string.IsNullOrWhiteSpace(styleName)
                || string.Equals(style.Name, styleName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(style => SearchStyle(style, itemType, search))
            .OfType<T>()
            .ToArray();
        var selected = !string.IsNullOrWhiteSpace(exactName)
            ? candidates.FirstOrDefault(item => string.Equals(
                item.Name, exactName, StringComparison.OrdinalIgnoreCase))
            : candidates.FirstOrDefault();
        return selected ?? throw new InvalidOperationException(
            $"No matching {itemType} style item was found. Search={search}, Name={exactName}");
    }

    private static void ApplyOptionalStyle(
        MapSurround surround,
        string surroundType,
        JsonElement payload)
    {
        var styleItemName = BridgeSupport.String(payload, "style_item_name");
        var search = BridgeSupport.String(payload, "style_search", styleItemName);
        if (string.IsNullOrWhiteSpace(search) && string.IsNullOrWhiteSpace(styleItemName))
        {
            return;
        }
        StyleItem item = surroundType.ToLowerInvariant() switch
        {
            "legend" => ResolveStyleItem<LegendStyleItem>(
                "legend", BridgeSupport.String(payload, "style_name"), styleItemName, search ?? string.Empty),
            "north_arrow" or "northarrow" => ResolveStyleItem<NorthArrowStyleItem>(
                "north_arrow", BridgeSupport.String(payload, "style_name"), styleItemName, search ?? string.Empty),
            "scale_bar" or "scalebar" => ResolveStyleItem<ScaleBarStyleItem>(
                "scale_bar", BridgeSupport.String(payload, "style_name"), styleItemName, search ?? string.Empty),
            _ => throw new ArgumentException($"Unsupported map surround style type: {surroundType}"),
        };
        surround.ApplyStyle(item);
    }

    private static MapSurroundInfo SurroundInfo(string surroundType, string frameName)
    {
        return surroundType.ToLowerInvariant() switch
        {
            "legend" => new LegendInfo { MapFrameName = frameName },
            "north_arrow" or "northarrow" => new NorthArrowInfo { MapFrameName = frameName },
            "scale_bar" or "scalebar" => new ScaleBarInfo { MapFrameName = frameName },
            _ => throw new ArgumentException($"Unsupported map surround type: {surroundType}"),
        };
    }

    private static CIMMapGrid ParseGrid(string gridType, string cimJson)
    {
        return gridType.ToLowerInvariant() switch
        {
            "graticule" or "cimgraticule" => CIMGraticule.FromJson(
                cimJson, new JsonDeserializationSettings()),
            "measured" or "measured_grid" or "cimmeasuredgrid" => CIMMeasuredGrid.FromJson(
                cimJson, new JsonDeserializationSettings()),
            _ => throw new ArgumentException($"Unsupported map grid CIM type: {gridType}"),
        };
    }

    private static void ApplyElementTransform(MapSurround surround, JsonElement payload)
    {
        if (payload.TryGetProperty("x", out _))
        {
            surround.SetX(BridgeSupport.Double(payload, "x"));
        }
        if (payload.TryGetProperty("y", out _))
        {
            surround.SetY(BridgeSupport.Double(payload, "y"));
        }
        if (payload.TryGetProperty("width", out _))
        {
            surround.SetWidth(Math.Max(0.01, BridgeSupport.Double(payload, "width")));
        }
        if (payload.TryGetProperty("height", out _))
        {
            surround.SetHeight(Math.Max(0.01, BridgeSupport.Double(payload, "height")));
        }
        if (payload.TryGetProperty("rotation", out _))
        {
            surround.SetRotation(BridgeSupport.Double(payload, "rotation"));
        }
        if (payload.TryGetProperty("visible", out _))
        {
            surround.SetVisible(BridgeSupport.Bool(payload, "visible", true));
        }
        if (payload.TryGetProperty("locked", out _))
        {
            surround.SetLocked(BridgeSupport.Bool(payload, "locked", false));
        }
    }

    private static void ApplySurroundParameters(MapSurround surround, JsonElement payload)
    {
        if (surround is Legend legend)
        {
            var definition = (CIMLegend)legend.GetDefinition();
            if (payload.TryGetProperty("title", out _))
            {
                definition.Title = BridgeSupport.String(payload, "title", string.Empty);
            }
            if (payload.TryGetProperty("show_title", out _))
            {
                definition.ShowTitle = BridgeSupport.Bool(payload, "show_title", true);
            }
            if (payload.TryGetProperty("auto_add", out _))
            {
                definition.AutoAdd = BridgeSupport.Bool(payload, "auto_add", true);
            }
            legend.SetDefinition(definition);
        }
        else if (surround is ScaleBar scaleBar)
        {
            var definition = (CIMScaleBar)scaleBar.GetDefinition();
            if (payload.TryGetProperty("units", out _))
            {
                definition.Units = ParseUnit(BridgeSupport.String(payload, "units"));
            }
            if (payload.TryGetProperty("unit_label", out _))
            {
                definition.UnitLabel = BridgeSupport.String(payload, "unit_label", string.Empty);
            }
            if (payload.TryGetProperty("divisions", out _))
            {
                definition.Divisions = Math.Clamp(BridgeSupport.Int(payload, "divisions", 4), 1, 20);
            }
            if (payload.TryGetProperty("subdivisions", out _))
            {
                definition.Subdivisions = Math.Clamp(BridgeSupport.Int(payload, "subdivisions", 0), 0, 20);
            }
            var division = BridgeSupport.Double(payload, "division_value", 0);
            if (division > 0)
            {
                definition.Division = division;
                definition.FittingStrategy = ScaleBarFittingStrategy.AdjustFrame;
            }
            scaleBar.SetDefinition(definition);
        }
    }

    private static LinearUnit ParseUnit(string? value)
    {
        return (value ?? string.Empty).ToLowerInvariant() switch
        {
            "meters" or "meter" or "m" => LinearUnit.Meters,
            "miles" or "mile" or "mi" => LinearUnit.Miles,
            "feet" or "foot" or "ft" => LinearUnit.Feet,
            _ => LinearUnit.Kilometers,
        };
    }
}
