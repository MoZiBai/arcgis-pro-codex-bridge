using System.Globalization;
using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class ViewFilterActions
{
    internal static async Task<object> GetAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(() => Describe(ResolveActiveView(requestedMap)));
    }

    internal static async Task<object> ConfigureAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestedMap = BridgeSupport.String(payload, "map_name");
        return await QueuedTask.Run<object>(async () =>
        {
            var view = ResolveActiveView(requestedMap);

            if (BridgeSupport.Bool(payload, "clear_time"))
            {
                view.Time = null!;
            }
            else if (payload.TryGetProperty("time", out var timeConfig)
                && timeConfig.ValueKind == JsonValueKind.Object)
            {
                var current = view.Time;
                var time = new TimeRange
                {
                    Start = timeConfig.TryGetProperty("start", out _)
                        ? ParseOptionalTime(BridgeSupport.String(timeConfig, "start"), "time.start")
                        : current?.Start,
                    End = timeConfig.TryGetProperty("end", out _)
                        ? ParseOptionalTime(BridgeSupport.String(timeConfig, "end"), "time.end")
                        : current?.End,
                    ExcludeStart = timeConfig.TryGetProperty("exclude_start", out _)
                        ? BridgeSupport.Bool(timeConfig, "exclude_start")
                        : current?.ExcludeStart ?? false,
                    ExcludeEnd = timeConfig.TryGetProperty("exclude_end", out _)
                        ? BridgeSupport.Bool(timeConfig, "exclude_end")
                        : current?.ExcludeEnd ?? false,
                    TimeReference = BuildTimeReference(timeConfig, current?.TimeReference),
                };
                view.Time = time;
            }

            if (BridgeSupport.Bool(payload, "clear_range"))
            {
                view.Range = null!;
            }
            else if (payload.TryGetProperty("range", out var rangeConfig)
                && rangeConfig.ValueKind == JsonValueKind.Object)
            {
                var current = view.Range;
                var range = new RangeExtent
                {
                    Min = rangeConfig.TryGetProperty("minimum", out var minimum)
                        ? OptionalDouble(minimum, "range.minimum")
                        : current?.Min,
                    Max = rangeConfig.TryGetProperty("maximum", out var maximum)
                        ? OptionalDouble(maximum, "range.maximum")
                        : current?.Max,
                    ExcludeMin = rangeConfig.TryGetProperty("exclude_minimum", out _)
                        ? BridgeSupport.Bool(rangeConfig, "exclude_minimum")
                        : current?.ExcludeMin ?? false,
                    ExcludeMax = rangeConfig.TryGetProperty("exclude_maximum", out _)
                        ? BridgeSupport.Bool(rangeConfig, "exclude_maximum")
                        : current?.ExcludeMax ?? false,
                    Exclusion = rangeConfig.TryGetProperty("exclusion", out _)
                        ? BridgeSupport.Bool(rangeConfig, "exclusion")
                        : current?.Exclusion ?? false,
                };
                view.Range = range;
            }

            if (payload.TryGetProperty("active_range_name", out _))
            {
                var rangeName = BridgeSupport.String(payload, "active_range_name") ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(rangeName)
                    && !view.RangeNames.Contains(rangeName, StringComparer.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"Range is not available in the active view: {rangeName}");
                }
                await view.SetActiveRangeName(rangeName);
            }

            if (BridgeSupport.Bool(payload, "clear_floor_filter"))
            {
                view.FloorFilter = new CIMFloorFilterSettings { Enabled = false };
            }
            else if (payload.TryGetProperty("floor_filter", out var floorConfig)
                && floorConfig.ValueKind == JsonValueKind.Object)
            {
                var floor = view.FloorFilter ?? new CIMFloorFilterSettings();
                ApplyFloorFilter(floor, floorConfig);
                view.FloorFilter = floor;
            }

            return Describe(view);
        });
    }

    private static object Describe(MapView view)
    {
        var time = view.Time;
        var range = view.Range;
        var floor = view.FloorFilter;
        return new
        {
            map = view.Map.Name,
            time = time is null ? null : new
            {
                start = FormatTime(time.Start),
                end = FormatTime(time.End),
                exclude_start = time.ExcludeStart,
                exclude_end = time.ExcludeEnd,
                time_reference = DescribeTimeReference(time.TimeReference),
            },
            range = range is null ? null : new
            {
                minimum = range.Min,
                maximum = range.Max,
                exclude_minimum = range.ExcludeMin,
                exclude_maximum = range.ExcludeMax,
                exclusion = range.Exclusion,
            },
            active_range_name = view.ActiveRangeName,
            range_names = view.RangeNames,
            floor_filter = floor is null ? null : new
            {
                enabled = floor.Enabled,
                minimized = floor.Minimized,
                long_names = floor.LongNames,
                pinned_levels = floor.PinnedLevels,
                focus_selected_facility = floor.FocusSelectedFacility,
                selected_site_id = floor.SelectedSiteID,
                selected_facility_id = floor.SelectedFacilityID,
                selected_level_id = floor.SelectedLevelID,
                selected_vertical_order = floor.SelectedVerticalOrder,
                use_vertical_order = floor.UseVerticalOrder,
                site_facility_ids = floor.SiteFacilityIDs ?? Array.Empty<string>(),
                site_level_ids = floor.SiteLevelIDs ?? Array.Empty<string>(),
                floor_visibility_mode = floor.FloorVisibilityMode.ToString(),
                cim_json = floor.ToJson(),
            },
            source_data_modified = false,
        };
    }

    private static object? DescribeTimeReference(TimeReference? reference)
    {
        return reference is null ? null : new
        {
            time_zone_iana_id = reference.TimeZoneIanaID,
            time_zone_name_id = reference.TimeZoneNameID,
            respects_daylight_saving_time = reference.RespectsDaylightSavingTime,
            respects_dynamic_adjustment_rules = reference.RespectsDynamicAdjustmentRules,
        };
    }

    private static TimeReference? BuildTimeReference(
        JsonElement config,
        TimeReference? current)
    {
        if (!config.TryGetProperty("time_reference", out var referenceConfig))
        {
            return current;
        }
        if (referenceConfig.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (referenceConfig.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("time.time_reference must be an object or null");
        }
        var reference = current ?? new TimeReference();
        if (referenceConfig.TryGetProperty("time_zone_iana_id", out _))
            reference.TimeZoneIanaID = BridgeSupport.String(referenceConfig, "time_zone_iana_id");
        if (referenceConfig.TryGetProperty("time_zone_name_id", out _))
            reference.TimeZoneNameID = BridgeSupport.String(referenceConfig, "time_zone_name_id");
        if (referenceConfig.TryGetProperty("respects_daylight_saving_time", out _))
            reference.RespectsDaylightSavingTime = BridgeSupport.Bool(referenceConfig, "respects_daylight_saving_time");
        if (referenceConfig.TryGetProperty("respects_dynamic_adjustment_rules", out _))
            reference.RespectsDynamicAdjustmentRules = BridgeSupport.Bool(referenceConfig, "respects_dynamic_adjustment_rules");
        return reference;
    }

    private static void ApplyFloorFilter(CIMFloorFilterSettings floor, JsonElement config)
    {
        if (config.TryGetProperty("enabled", out _))
            floor.Enabled = BridgeSupport.Bool(config, "enabled");
        if (config.TryGetProperty("minimized", out _))
            floor.Minimized = BridgeSupport.Bool(config, "minimized");
        if (config.TryGetProperty("long_names", out _))
            floor.LongNames = BridgeSupport.Bool(config, "long_names");
        if (config.TryGetProperty("pinned_levels", out _))
            floor.PinnedLevels = BridgeSupport.Bool(config, "pinned_levels");
        if (config.TryGetProperty("focus_selected_facility", out _))
            floor.FocusSelectedFacility = BridgeSupport.Bool(config, "focus_selected_facility");
        if (config.TryGetProperty("selected_site_id", out _))
            floor.SelectedSiteID = BridgeSupport.String(config, "selected_site_id");
        if (config.TryGetProperty("selected_facility_id", out _))
            floor.SelectedFacilityID = BridgeSupport.String(config, "selected_facility_id");
        if (config.TryGetProperty("selected_level_id", out _))
            floor.SelectedLevelID = BridgeSupport.String(config, "selected_level_id");
        if (config.TryGetProperty("selected_vertical_order", out _))
            floor.SelectedVerticalOrder = BridgeSupport.Int(config, "selected_vertical_order");
        if (config.TryGetProperty("use_vertical_order", out _))
            floor.UseVerticalOrder = BridgeSupport.Bool(config, "use_vertical_order");
        if (config.TryGetProperty("site_facility_ids", out _))
            floor.SiteFacilityIDs = BridgeSupport.Strings(config, "site_facility_ids");
        if (config.TryGetProperty("site_level_ids", out _))
            floor.SiteLevelIDs = BridgeSupport.Strings(config, "site_level_ids");
        if (config.TryGetProperty("floor_visibility_mode", out _))
        {
            var raw = BridgeSupport.String(config, "floor_visibility_mode");
            if (!Enum.TryParse<FloorVisibilityMode>(raw, true, out var mode))
                throw new ArgumentException($"Unsupported floor_visibility_mode: {raw}");
            floor.FloorVisibilityMode = mode;
        }
    }

    private static string? FormatTime(DateTime? value)
    {
        return value?.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTime? ParseOptionalTime(string? value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces,
            out var parsed))
        {
            throw new ArgumentException($"{parameter} must be an ISO-8601 date/time");
        }
        return parsed;
    }

    private static double? OptionalDouble(JsonElement value, string parameter)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
        {
            throw new ArgumentException($"{parameter} must be a number or null");
        }
        return number;
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
}
