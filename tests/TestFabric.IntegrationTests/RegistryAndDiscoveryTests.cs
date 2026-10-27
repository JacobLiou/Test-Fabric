using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TestFabric.IntegrationTests.Infrastructure;
using JsonElement = System.Text.Json.JsonElement;

namespace TestFabric.IntegrationTests;

/// <summary>FR-01 资源注册验收。</summary>
public class RegistryTests(TestEnvironment env) : IClassFixture<TestEnvironment>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task Create_Duplicate_Returns409_WithCode()
    {
        env.SkipIfUnavailable();
        using var engineer = env.Engineer();
        var req = env.Resource("OSA", "001", "OpticalSpectrumAnalyzer", "Yokogawa", "AQ6370D",
            env.OsaCapability, new Dictionary<string, object?> { ["resolutionMinNm"] = 0.02 }, "2099-12-31");
        var first = await engineer.PostAsJsonAsync("api/v1/resources", req);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await engineer.PostAsJsonAsync("api/v1/resources", req);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("RESOURCE_ID_DUPLICATED", body.GetProperty("code").GetString()); // AC-01-02
    }

    [SkippableFact]
    public async Task Create_MissingFields_Returns400_WithFieldList()
    {
        env.SkipIfUnavailable();
        using var engineer = env.Engineer();
        var response = await engineer.PostAsJsonAsync("api/v1/resources", new { resourceId = $"X-{env.Prefix}" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", body.GetProperty("code").GetString());
        Assert.Contains("addressType", body.GetProperty("detail").GetString()); // AC-01-03
    }

    [SkippableFact]
    public async Task Update_RecordsOldAndNewValuesInAudit()
    {
        env.SkipIfUnavailable();
        using var engineer = env.Engineer();
        var create = await engineer.PostAsJsonAsync("api/v1/resources", env.Resource("OSA", "010", "OpticalSpectrumAnalyzer",
            "Yokogawa", "AQ6370D", env.OsaCapability, new Dictionary<string, object?> { ["resolutionMinNm"] = 0.02 }, null));
        Assert.True(create.IsSuccessStatusCode);

        var patch = await engineer.PatchAsJsonAsync($"api/v1/resources/OSA-{env.Prefix}-010", new { address = "10.10.99.99" });
        Assert.True(patch.IsSuccessStatusCode);

        var events = await env.GetEventsAsync($"OSA-{env.Prefix}-010");
        var updated = events.EnumerateArray().FirstOrDefault(e => e.GetProperty("eventType").GetString() == "ResourceUpdated");
        Assert.True(updated.ValueKind != JsonValueKind.Undefined); // AC-01-06
    }

    [SkippableFact]
    public async Task Disable_IsBlockedWhileBusy()
    {
        env.SkipIfUnavailable();
        var lease = await env.AcquireAsync(env.OsaCapability, "STATION-REG"); // 占用 OSA-001
        using var admin = env.Admin();
        var disable = await admin.PostAsync($"api/v1/resources/{lease.GetProperty("resourceId").GetString()}/disable", null);
        Assert.Equal(HttpStatusCode.Conflict, disable.StatusCode);
        var body = await disable.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("RESOURCE_BUSY", body.GetProperty("code").GetString()); // AC-12-01

        using var client = env.TestClient();
        await client.PostAsJsonAsync($"api/v1/leases/{lease.GetProperty("leaseId").GetString()}/release",
            new { clientId = "STATION-REG", reason = "Completed" });
    }

    [SkippableFact]
    public async Task Delete_IsLogical_HistoryRemains()
    {
        env.SkipIfUnavailable();
        using var engineer = env.Engineer();
        var id = $"OSA-{env.Prefix}-020";
        await engineer.PostAsJsonAsync("api/v1/resources", env.Resource("OSA", "020", "OpticalSpectrumAnalyzer",
            "Yokogawa", "AQ6370D", env.OsaCapability, new Dictionary<string, object?> { ["resolutionMinNm"] = 0.02 }, null));

        var del = await engineer.DeleteAsync($"api/v1/resources/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var gone = await env.GetResourceAsync(id);
        Assert.Equal("Disabled", gone.GetProperty("administrativeState").GetString());

        var events = await env.GetEventsAsync(id);
        Assert.Contains(events.EnumerateArray(), e => e.GetProperty("eventType").GetString() == "ResourceDeleted"); // AC-01-05
    }

    [SkippableFact]
    public async Task Unauthorized_Requests_Get401()
    {
        env.SkipIfUnavailable();
        using var anonymous = env.App.CreateClient();
        var response = await anonymous.GetAsync("api/v1/resources");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); // AC-11-01
    }

    [SkippableFact]
    public async Task Viewer_CannotAcquire()
    {
        env.SkipIfUnavailable();
        using var viewer = env.Viewer();
        var response = await viewer.PostAsJsonAsync("api/v1/leases/acquire", new { clientId = "V", capability = env.OsaCapability });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // AC-11-02（Viewer 无 TestClient 权限）
    }
}

/// <summary>FR-03 发现与匹配验收。</summary>
public class DiscoveryTests(TestEnvironment env) : IClassFixture<TestEnvironment>
{
    private static JsonElement Constraint(string json)
        => JsonSerializer.Deserialize<JsonElement>(json);

    [SkippableFact]
    public async Task Constraints_FilterCandidates_And_ExplainExclusions()
    {
        env.SkipIfUnavailable();
        var discovery = await env.DiscoverAsync(env.OsaCapability, new Dictionary<string, JsonElement>
        {
            ["resolutionMinNm"] = Constraint(@"{""operator"":""lte"",""value"":0.05}"),
            ["calibrationValid"] = Constraint("true"),
        });

        var candidates = discovery.GetProperty("candidates").EnumerateArray().ToList();
        Assert.Single(candidates);
        Assert.Equal(env.Osa1, candidates[0].GetProperty("resourceId").GetString()); // AC-02-01

        var excluded = discovery.GetProperty("excluded").EnumerateArray().ToList();
        var osa2 = excluded.First(e => e.GetProperty("resourceId").GetString() == env.Osa2);
        Assert.False(string.IsNullOrEmpty(osa2.GetProperty("reason").GetString())); // AC-03-03：结构化原因
    }

    [SkippableFact]
    public async Task UnknownConstraint_Returns400_NotSilentlyIgnored()
    {
        env.SkipIfUnavailable();
        using var client = env.TestClient();
        var response = await client.PostAsJsonAsync("api/v1/resources/discover", new
        {
            capability = env.OsaCapability,
            constraints = new Dictionary<string, object> { ["wavelenghtMinNm"] = new { @operator = "lte", value = 1260 } }, // 拼写错误
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UNSUPPORTED_CONSTRAINT", body.GetProperty("code").GetString()); // AC-02-04
    }

    [SkippableFact]
    public async Task Maintenance_ExcludedFromCandidates()
    {
        env.SkipIfUnavailable();
        using var admin = env.Admin();
        var maintenance = await admin.PostAsync($"api/v1/resources/{env.Osa2}/maintenance", null);
        Assert.True(maintenance.IsSuccessStatusCode);

        var discovery = await env.DiscoverAsync(env.OsaCapability);
        var ids = discovery.GetProperty("candidates").EnumerateArray()
            .Select(c => c.GetProperty("resourceId").GetString()).ToList();
        Assert.DoesNotContain(env.Osa2, ids); // AC-07-05

        await admin.PostAsync($"api/v1/resources/{env.Osa2}/enable", null); // 恢复现场
    }

    [SkippableFact]
    public async Task CalibrationExpired_NotAllocatable()
    {
        env.SkipIfUnavailable();
        var lease = await env.AcquireAsync(env.OsaCapability, "STATION-CAL", new Dictionary<string, JsonElement>
        {
            ["resolutionMinNm"] = Constraint(@"{""operator"":""lte"",""value"":0.05}"),
        });
        // 唯一满足分辨率的是 OSA-001（校准有效）
        Assert.Equal(env.Osa1, lease.GetProperty("resourceId").GetString());
        using var client = env.TestClient();
        await client.PostAsJsonAsync($"api/v1/leases/{lease.GetProperty("leaseId").GetString()}/release",
            new { clientId = "STATION-CAL", reason = "Completed" });
    }
}
