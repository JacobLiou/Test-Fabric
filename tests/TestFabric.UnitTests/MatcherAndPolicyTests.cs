using TestFabric.Contracts;
using TestFabric.Core.Matching;
using Xunit;

namespace TestFabric.UnitTests;

/// <summary>约束求值真值表（评审 P1：钉死 matcher 语义，防止双关解释）。</summary>
public class ConstraintEvaluatorTests
{
    private static bool Eval(string op, object? constraintValue, object? propertyValue, out string? reason)
        => ConstraintEvaluator.TryEvaluate("x", new ConstraintSpec(op, constraintValue), propertyValue, out reason);

    // PRD AC-02-01：设备 600–1700nm、0.02 分辨率，请求 1260–1650nm、≤0.05nm → 匹配
    [Theory]
    [InlineData("lte", 1260, 600.0, true)]   // wavelengthMinNm <= 1260：设备下限 600 满足
    [InlineData("gte", 1650, 1700.0, true)]  // wavelengthMaxNm >= 1650：设备上限 1700 满足
    [InlineData("lte", 0.05, 0.02, true)]    // resolutionMinNm <= 0.05：设备 0.02 更细，满足
    [InlineData("lte", 0.05, 0.1, false)]    // AC-02-02：设备 0.1 粗于 0.05 → 排除
    [InlineData("gte", 1700, 1700.0, true)]   // 边界相等
    [InlineData("gt", 1700, 1700.0, false)]   // 严格大于
    [InlineData("eq", 4, 4.0, true)]          // int 与 double 数值相等
    [InlineData("neq", 5, 4.0, true)]
    public void NumericComparisons(string op, object constraintValue, object propertyValue, bool expected)
    {
        Assert.Equal(expected, Eval(op, constraintValue, propertyValue, out _));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    public void BoolEquality(object constraintValue, object propertyValue, bool expected)
        => Assert.Equal(expected, Eval("eq", constraintValue, propertyValue, out _));

    [Theory]
    [InlineData("Healthy", "Healthy", true)]
    [InlineData("Degraded", "Healthy", false)]
    public void StringEquality(string constraintValue, string propertyValue, bool expected)
        => Assert.Equal(expected, Eval("eq", constraintValue, propertyValue, out _));

    [Fact]
    public void InOperator_MatchesListMember()
    {
        var list = new List<object?> { "A", "B", "C" };
        Assert.True(Eval("in", list, "B", out _));
        Assert.False(Eval("in", list, "D", out _));
    }

    [Fact]
    public void MissingProperty_ExcludedWithReason()
    {
        Assert.False(Eval("eq", 1, null, out var reason));
        Assert.Contains("not declared", reason);
    }

    [Fact]
    public void TypeMismatch_DoesNotThrow_AndExcludes()
    {
        // 属性是字符串、约束是数值 → 走字符串比较失败，不抛异常
        Assert.False(Eval("eq", 600, "600nm", out _));
    }

    [Fact]
    public void NumericReason_IsHumanReadable()
    {
        Eval("lte", 0.05, 0.1, out var reason);
        Assert.Contains("0.1", reason);
        Assert.Contains("lte 0.05", reason);
    }
}

/// <summary>约束解析：两种写法 + 校验规则。</summary>
public class ConstraintParserTests
{
    private static Dictionary<string, System.Text.Json.JsonElement> Element(params (string Key, string Json)[] pairs)
    {
        var dict = new Dictionary<string, System.Text.Json.JsonElement>();
        foreach (var (key, json) in pairs)
            dict[key] = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
        return dict;
    }

    [Fact]
    public void ParsesObjectForm()
    {
        var parsed = ConstraintParser.Parse(Element(("wavelengthMinNm", @"{""operator"":""lte"",""value"":1260}")));
        Assert.Equal("lte", parsed["wavelengthMinNm"].Operator);
        Assert.Equal(1260d, parsed["wavelengthMinNm"].Value);
    }

    [Fact]
    public void ParsesBareValue_AsEq()
    {
        var parsed = ConstraintParser.Parse(Element(("calibrationValid", "true")));
        Assert.Equal("eq", parsed["calibrationValid"].Operator);
        Assert.Equal(true, parsed["calibrationValid"].Value);
    }

    [Fact]
    public void UnsupportedOperator_Rejected()
    {
        Assert.Throws<TestFabric.Core.Domain.FabricException>(() =>
            ConstraintParser.Parse(Element(("x", @"{""operator"":""regex"",""value"":""a""}"))));
    }

    [Fact]
    public void EnsureDefined_UnknownField_Throws_UnsupportedConstraint()
    {
        var schema = new CapabilitySchema(new Dictionary<string, CapabilityPropertyDef>
        {
            ["wavelengthMinNm"] = new(CapabilityPropertyDef.Number),
        });
        var constraints = new Dictionary<string, ConstraintSpec> { ["wavelenghtMinNm"] = new("lte", 1260) }; // 拼写错误
        var ex = Assert.Throws<TestFabric.Core.Domain.FabricException>(() =>
            ConstraintParser.EnsureDefined(schema, constraints));
        Assert.Equal(Contracts.ErrorCodes.UnsupportedConstraint, ex.Code);
    }

    [Fact]
    public void EnsureDefined_BuiltinProperty_Allowed()
    {
        var schema = new CapabilitySchema([]);
        ConstraintParser.EnsureDefined(schema, new Dictionary<string, ConstraintSpec>
        {
            ["calibrationValid"] = new("eq", true),
        });
    }
}

/// <summary>SDK 心跳策略：TTL/3，钳制 [2s, 30s]。</summary>
public class HeartbeatPolicyTests
{
    [Theory]
    [InlineData(60, 20)]
    [InlineData(6, 2)]
    [InlineData(3, 2)]      // 低于下限 → 2s
    [InlineData(300, 30)]   // 高于上限 → 30s
    public void Interval_Clamped(int ttl, int expectedSeconds)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), Sdk.LeaseHandle.ComputeHeartbeatInterval(ttl));
}

/// <summary>状态机迁移表一致性（FR-07）。</summary>
public class StateTransitionTests
{
    [Theory]
    [InlineData("Available", "Busy")]
    [InlineData("Busy", "Recovering")]
    [InlineData("Recovering", "Available")]
    [InlineData("Recovering", "Error")]
    [InlineData("Unknown", "Available")]
    public void Allowed(string from, string to)
        => Assert.True(Core.Domain.States.IsAllowedOperationalTransition(from, to));

    [Theory]
    [InlineData("Available", "Busy2")]
    [InlineData("Error", "Busy")]      // Error 不能直接回 Busy
    [InlineData("Busy", "Available")]  // 必须经 Recovering
    public void Forbidden(string from, string to)
        => Assert.False(Core.Domain.States.IsAllowedOperationalTransition(from, to));
}
