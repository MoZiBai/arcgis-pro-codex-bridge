using System.Text.Json;
using ArcGIS.Core.Data;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace ArcGISCodexBridgeAddin;

internal static class AttributeRuleActions
{
    internal static async Task<object> EvaluateAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!BridgeSupport.Bool(payload, "confirm_evaluate", false))
            throw new ArgumentException("confirm_evaluate=true is required because attribute-rule evaluation can update validation error tables");
        var memberName = BridgeSupport.String(payload, "member_name")
            ?? throw new ArgumentException("member_name is required");
        return await QueuedTask.Run<object>(() =>
        {
            var map = BridgeSupport.ResolveMap(BridgeSupport.String(payload, "map_name"));
            var member = ResolveMember(map, memberName);
            using var table = member switch
            {
                BasicFeatureLayer layer => layer.GetTable(),
                StandaloneTable standalone => standalone.GetTable(),
                _ => throw new InvalidOperationException("Map member does not expose a table"),
            };
            using var datastore = table.GetDatastore();
            if (datastore is not Geodatabase geodatabase)
                throw new InvalidOperationException("Attribute-rule evaluation requires a geodatabase-backed map member");
            using var manager = geodatabase.GetAttributeRuleManager();
            if (!manager.IsEvaluationSupported())
                throw new InvalidOperationException("Attribute-rule evaluation is not supported by this geodatabase");
            var ruleType = ParseEnum(
                BridgeSupport.String(payload, "rule_type", "Validation"),
                AttributeRuleType.Validation);
            var versionScope = ParseEnum(
                BridgeSupport.String(payload, "version_scope", "EntireVersion"),
                VersionEvaluationScope.EntireVersion);
            AttributeRuleEvaluationDescription description;
            if (payload.TryGetProperty("extent", out _))
            {
                description = new AttributeRuleEvaluationDescription(
                    ruleType,
                    versionScope,
                    BridgeSupport.Bounds(payload, "extent", 0, 0, 1, 1));
            }
            else
            {
                description = new AttributeRuleEvaluationDescription(ruleType, versionScope);
            }
            description.ServiceSynchronizationType = ParseEnum(
                BridgeSupport.String(payload, "service_synchronization", "Synchronous"),
                ServiceSynchronizationType.Synchronous);
            var result = manager.Evaluate(description);
            return new
            {
                map = map.Name,
                member = member.Name,
                rule_type = description.AttributeRuleType.ToString(),
                version_scope = description.VersionEvaluationScope.ToString(),
                service_synchronization = description.ServiceSynchronizationType.ToString(),
                number_of_errors = result.NumberOfErrors,
                project_modified = false,
                source_data_modified = true,
                project_saved = false,
                confirmation_required = true,
            };
        });
    }

    private static MapMember ResolveMember(Map map, string name)
    {
        return map.GetLayersAsFlattenedList().OfType<BasicFeatureLayer>().Cast<MapMember>()
            .Concat(map.GetStandaloneTablesAsFlattenedList().Cast<MapMember>())
            .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Feature layer or standalone table not found: {name}");
    }

    private static T ParseEnum<T>(string? value, T fallback) where T : struct, Enum
    {
        return Enum.TryParse<T>(value, true, out var result) ? result : fallback;
    }
}
