using System.Text.Json;

namespace TestFabric.Core.Persistence;

/// <summary>
/// 将 System.Text.Json 的 JsonElement 树转成普通 CLR 值（double/bool/string/List/Dictionary）。
/// 读取 JSONB（schema_json / properties_json）后必须经过此转换，否则属性值是 boxed JsonElement，无法参与约束比较。
/// </summary>
public static class JsonPlain
{
    public static Dictionary<string, object?> ToDictionary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, object?>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            result[prop.Name] = ToValue(prop.Value);
        return result;
    }

    public static object? ToValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.GetDouble(),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => el.EnumerateArray().Select(ToValue).ToList(),
        JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => (object?)ToValue(p.Value)),
        _ => null,
    };
}
