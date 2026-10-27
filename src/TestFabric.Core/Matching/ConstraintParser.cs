using System.Text.Json;
using TestFabric.Contracts;
using TestFabric.Core.Domain;

namespace TestFabric.Core.Matching;

/// <summary>
/// 约束解析：JsonElement → ConstraintSpec。
/// 支持两种写法："resolutionMinNm": {"operator":"lte","value":0.05} 与裸值 "calibrationValid": true。
/// </summary>
public static class ConstraintParser
{
    public static readonly IReadOnlySet<string> SupportedOperators = new HashSet<string>
        { "eq", "neq", "gt", "gte", "lt", "lte", "in" };

    /// <summary>内置伪属性：不来自能力 schema，由平台从资源状态计算。</summary>
    public static readonly IReadOnlySet<string> BuiltinProperties = new HashSet<string>
        { "calibrationValid", "healthState", "operationalState", "administrativeState" };

    public static Dictionary<string, ConstraintSpec> Parse(Dictionary<string, JsonElement>? raw)
    {
        var result = new Dictionary<string, ConstraintSpec>();
        if (raw is null) return result;

        foreach (var (name, el) in raw)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw FabricException.Validation("constraint name must not be empty");

            if (el.ValueKind == JsonValueKind.Object)
            {
                if (!el.TryGetProperty("operator", out var opEl) || opEl.ValueKind != JsonValueKind.String)
                    throw FabricException.Validation($"constraint '{name}': object form requires a string 'operator'");
                var op = opEl.GetString()!;
                if (!SupportedOperators.Contains(op))
                    throw FabricException.Validation($"constraint '{name}': unsupported operator '{op}' (supported: {string.Join(", ", SupportedOperators)})");
                object? value = el.TryGetProperty("value", out var v) ? ToValue(name, v) : null;
                if (op == "in" && value is not IReadOnlyList<object?>)
                    throw FabricException.Validation($"constraint '{name}': operator 'in' requires an array value");
                result[name] = new ConstraintSpec(op, value);
            }
            else
            {
                result[name] = new ConstraintSpec("eq", ToValue(name, el));
            }
        }
        return result;
    }

    private static object? ToValue(string name, JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.GetDouble(),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => el.EnumerateArray().Select(item => ToValue(name, item)).ToList(),
        _ => throw FabricException.Validation($"constraint '{name}': unsupported JSON value kind '{el.ValueKind}'"),
    };

    /// <summary>校验约束字段名：必须在能力 schema 中定义，或是内置伪属性（AC-02-04，不得静默忽略）。</summary>
    public static void EnsureDefined(CapabilitySchema schema, IReadOnlyDictionary<string, ConstraintSpec> constraints)
    {
        foreach (var name in constraints.Keys)
        {
            if (BuiltinProperties.Contains(name)) continue;
            if (!schema.Properties.ContainsKey(name))
                throw new FabricException(ErrorCodes.UnsupportedConstraint,
                    $"constraint '{name}' is not defined in capability schema (defined: {string.Join(", ", schema.Properties.Keys)}; builtins: {string.Join(", ", BuiltinProperties)})",
                    new() { ["field"] = name });
        }
    }
}
