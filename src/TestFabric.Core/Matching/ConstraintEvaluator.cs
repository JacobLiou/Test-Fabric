using TestFabric.Contracts;
using TestFabric.Core.Domain;

namespace TestFabric.Core.Matching;

/// <summary>
/// 约束求值器。统一语义：资源属性值 (operator) 请求值。
/// 纯函数、无 IO —— 由单元测试真值表覆盖（评审 P1：钉死 matcher 语义）。
/// </summary>
public static class ConstraintEvaluator
{
    /// <summary>返回是否满足；不满足或无法求值时给出人类可读原因。</summary>
    public static bool TryEvaluate(string name, ConstraintSpec spec, object? propertyValue, out string? reason)
    {
        reason = null;

        if (propertyValue is null)
        {
            reason = $"property '{name}' is not declared by the resource";
            return false;
        }

        if (spec.Operator == "in")
            return EvaluateIn(name, spec, propertyValue, out reason);

        // 数值比较
        if (TryAsDouble(propertyValue, out var pv) && TryAsDouble(spec.Value, out var cv))
        {
            var ok = spec.Operator switch
            {
                "eq" => Math.Abs(pv - cv) < 1e-9,
                "neq" => Math.Abs(pv - cv) >= 1e-9,
                "gt" => pv > cv,
                "gte" => pv >= cv,
                "lt" => pv < cv,
                "lte" => pv <= cv,
                _ => throw new InvalidOperationException($"operator '{spec.Operator}' not handled"),
            };
            if (!ok) reason = $"'{name}' value {pv} does not satisfy {spec.Operator} {cv}";
            return ok;
        }

        // 布尔比较：仅 eq/neq 有意义
        if (propertyValue is bool && spec.Value is bool)
        {
            var equal = Equals(propertyValue, spec.Value);
            var ok = spec.Operator is "eq" or "neq" ? (spec.Operator == "eq") == equal : false;
            if (!ok) reason = $"'{name}' value {Format(propertyValue)} does not satisfy {spec.Operator} {Format(spec.Value)}";
            return ok;
        }

        // 字符串比较
        var ps = propertyValue.ToString() ?? "";
        var cs = spec.Value?.ToString() ?? "";
        var cmp = string.CompareOrdinal(ps, cs);
        var stringOk = spec.Operator switch
        {
            "eq" or "gte" or "lte" => cmp == 0,   // 字符串上 gte/lte 按相等处理
            "neq" => cmp != 0,
            "gt" => cmp > 0,
            "lt" => cmp < 0,
            _ => throw new InvalidOperationException($"operator '{spec.Operator}' not handled"),
        };
        if (!stringOk) reason = $"'{name}' value '{ps}' does not satisfy {spec.Operator} '{cs}'";
        return stringOk;
    }

    private static bool EvaluateIn(string name, ConstraintSpec spec, object propertyValue, out string? reason)
    {
        reason = null;
        var list = (spec.Value as System.Collections.IEnumerable)?.Cast<object?>().ToList() ?? [];
        var matched = list.Any(v => ValuesEqual(propertyValue, v));
        if (!matched) reason = $"'{name}' value {Format(propertyValue)} not in [{string.Join(", ", list.Select(Format))}]";
        return matched;
    }

    private static bool ValuesEqual(object a, object? b)
    {
        if (TryAsDouble(a, out var da) && TryAsDouble(b, out var db)) return Math.Abs(da - db) < 1e-9;
        return string.Equals(a.ToString(), b?.ToString(), StringComparison.Ordinal);
    }

    private static bool TryAsDouble(object? v) => TryAsDouble(v, out _);

    private static bool TryAsDouble(object? v, out double d)
    {
        switch (v)
        {
            case double dd: d = dd; return true;
            case int i: d = i; return true;
            case long l: d = l; return true;
            case float f: d = f; return true;
            case decimal m: d = (double)m; return true;
            default: d = 0; return false;
        }
    }

    private static string Format(object? v) => v switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        _ => v.ToString() ?? "",
    };
}
