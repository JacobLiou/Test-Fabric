namespace TestFabric.Contracts;

/// <summary>
/// 约束定义。语义统一为：资源属性值 (operator) 请求值。
/// 例：wavelengthMinNm { operator: "lte", value: 1260 } 表示 资源的波长下限 <= 1260nm。
/// value 为裸值时等价于 operator = "eq"。
/// 内置伪属性：calibrationValid(bool)、healthState(string)、operationalState(string)。
/// </summary>
public record ConstraintSpec(string Operator, object? Value);

/// <summary>
/// 约束以原始 JsonElement 提交，支持两种写法：
///   "resolutionMinNm": { "operator": "lte", "value": 0.05 }  或  "calibrationValid": true
/// 由 Core 的 ConstraintParser 统一解析。
/// </summary>
public record DiscoverRequest(
    string Capability,
    Dictionary<string, System.Text.Json.JsonElement>? Constraints = null);

public record DiscoverResponse(
    string Capability,
    IReadOnlyList<ResourceDto> Candidates,
    IReadOnlyList<ExcludedResource> Excluded);

public record ExcludedResource(string ResourceId, string Reason);
