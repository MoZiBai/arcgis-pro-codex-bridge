using System.Text.Json;
using ArcGIS.Desktop.Core.Geoprocessing;

namespace ArcGISCodexBridgeAddin;

internal static class GeoprocessingActions
{
    internal static async Task<object> RunAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var tool = BridgeSupport.String(payload, "tool")
            ?? throw new ArgumentException("tool is required");
        if (tool.StartsWith("ddd.", StringComparison.OrdinalIgnoreCase))
        {
            tool = "3d." + tool[4..];
        }
        if (payload.TryGetProperty("kwargs", out var kwargs)
            && kwargs.ValueKind == JsonValueKind.Object
            && kwargs.EnumerateObject().Any())
        {
            throw new ArgumentException(
                "The in-process ArcGIS Pro geoprocessing bridge currently accepts ordered args; kwargs are not supported.");
        }

        var rawArguments = payload.TryGetProperty("args", out var args)
            && args.ValueKind == JsonValueKind.Array
            ? args.EnumerateArray().Select(JsonValue).ToArray()
            : Array.Empty<object?>();
        var parameters = Geoprocessing.MakeValueArray(rawArguments);
        var environments = Geoprocessing.MakeEnvironmentArray(
            overwriteoutput: BridgeSupport.Bool(payload, "overwrite_output", false));
        var flags = GPExecuteToolFlags.None;
        if (BridgeSupport.Bool(payload, "add_outputs_to_map", true))
        {
            flags |= GPExecuteToolFlags.AddOutputsToMap;
        }

        var result = await Geoprocessing.ExecuteToolAsync(
            tool,
            parameters,
            environments,
            cancellationToken,
            null,
            flags);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is null)
        {
            throw new InvalidOperationException(
                $"ArcGIS Pro returned no geoprocessing result for tool: {tool}");
        }
        var messages = result.Messages?
            .Where(message => message is not null)
            .Select(message => new
            {
                type = message.Type.ToString(),
                text = message.Text,
            }).ToArray();
        return new
        {
            tool,
            failed = result.IsFailed,
            error_code = result.ErrorCode,
            return_value = result.ReturnValue,
            messages,
            added_outputs_to_map = BridgeSupport.Bool(payload, "add_outputs_to_map", true),
        };
    }

    private static object? JsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => value.EnumerateArray().Select(JsonValue).ToArray(),
            _ => value.GetRawText(),
        };
    }
}
