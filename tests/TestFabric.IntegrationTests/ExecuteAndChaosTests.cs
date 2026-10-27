using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TestFabric.IntegrationTests.Infrastructure;
using JsonElement = System.Text.Json.JsonElement;

namespace TestFabric.IntegrationTests;

/// <summary>FR-08 执行能力验收：统一接口、租约前置校验、错误码转换。</summary>
public class ExecuteTests(TestEnvironment env) : IClassFixture<TestEnvironment>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task Osa_FullFlow_Configure_Sweep_Trace_Peak_Abort()
    {
        env.SkipIfUnavailable();
        var lease = await env.AcquireAsync(env.OsaCapability, "STATION-OSA");
        var leaseId = lease.GetProperty("leaseId").GetString()!;
        using var client = env.TestClient();

        var configure = await client.PostAsJsonAsync($"api/v1/leases/{leaseId}/osa/configure",
            new { clientId = "STATION-OSA", startNm = 1260.0, stopNm = 1360.0, resolutionNm = 0.05 });
        configure.EnsureSuccessStatusCode(); // AC-08-01：统一配置接口

        var sweep = await client.PostAsJsonAsync($"api/v1/leases/{leaseId}/osa/single-sweep", new { clientId = "STATION-OSA" });
        sweep.EnsureSuccessStatusCode();
        var sweepBody = await sweep.Content.ReadFromJsonAsync<JsonElement>();
        var sweepId = sweepBody.GetProperty("sweepId").GetString()!;
        Assert.True(sweepBody.GetProperty("points").GetInt32() > 0);
        Assert.Equal(env.Osa1, sweepBody.GetProperty("resourceId").GetString());

        var trace = await client.PostAsJsonAsync($"api/v1/leases/{leaseId}/osa/get-trace", new { clientId = "STATION-OSA", sweepId });
        trace.EnsureSuccessStatusCode();
        var traceBody = await trace.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("nm", traceBody.GetProperty("unit").GetString());
        Assert.True(traceBody.GetProperty("valuesDbm").GetArrayLength() > 0);

        var peak = await client.PostAsJsonAsync($"api/v1/leases/{leaseId}/osa/get-peak", new { clientId = "STATION-OSA", sweepId });
        peak.EnsureSuccessStatusCode();
        var peakBody = await peak.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(peakBody.GetProperty("peakPowerDbm").GetDouble() < 0);
        Assert.Equal("dBm", peakBody.GetProperty("unit").GetString()); // AC-08-03：数值 + 单位 + 资源 + 版本

        var abort = await client.PostAsJsonAsync($"api/v1/leases/{leaseId}/osa/abort", new { clientId = "STATION-OSA" });
        abort.EnsureSuccessStatusCode();

        await client.PostAsJsonAsync($"api/v1/leases/{leaseId}/release", new { clientId = "STATION-OSA", reason = "Completed" });
    }

    [SkippableFact]
    public async Task PowerMeter_MeasureAndZero()
    {
        env.SkipIfUnavailable();
        var lease = await env.AcquireAsync(env.PmCapability, "STATION-PM");
        var leaseId = lease.GetProperty("leaseId").GetString()!;
        using var client = env.TestClient();

        var measure = await client.PostAsJsonAsync($"api/v1/leases/{leaseId}/power-meter/measure-power",
            new { clientId = "STATION-PM", channel = 1 });
        measure.EnsureSuccessStatusCode();
        var body = await measure.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("powerDbm").GetDouble() < 0);
        Assert.Equal("dBm", body.GetProperty("unit").GetString());

        var zero = await client.PostAsJsonAsync($"api/v1/leases/{leaseId}/power-meter/zero",
            new { clientId = "STATION-PM", channel = 1 });
        zero.EnsureSuccessStatusCode();

        var afterZero = await client.PostAsJsonAsync($"api/v1/leases/{leaseId}/power-meter/measure-power",
            new { clientId = "STATION-PM", channel = 1 });
        var zeroed = await afterZero.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(Math.Abs(zeroed.GetProperty("powerDbm").GetDouble()) < 0.5); // 清零后接近 0

        await client.PostAsJsonAsync($"api/v1/leases/{leaseId}/release", new { clientId = "STATION-PM", reason = "Completed" });
    }

    [SkippableFact]
    public async Task Execute_ByNonOwner_Rejected()
    {
        env.SkipIfUnavailable();
        var lease = await env.AcquireAsync(env.OsaCapability, "STATION-OWNER-A");
        var leaseId = lease.GetProperty("leaseId").GetString()!;
        using var client = env.TestClient();

        var response = await client.PostAsJsonAsync($"api/v1/leases/{leaseId}/osa/single-sweep", new { clientId = "STATION-OWNER-B" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("LEASE_OWNER_MISMATCH",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        await client.PostAsJsonAsync($"api/v1/leases/{leaseId}/release", new { clientId = "STATION-OWNER-A", reason = "Completed" });
    }

    [SkippableFact]
    public async Task Execute_WithoutLease_Rejected()
    {
        env.SkipIfUnavailable();
        using var client = env.TestClient();
        var response = await client.PostAsJsonAsync("api/v1/leases/L-DOES-NOT-EXIST/osa/single-sweep", new { clientId = "X" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("LEASE_NOT_FOUND",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()); // AC-08-04
    }

    [SkippableFact]
    public async Task Execute_AfterExpiry_Rejected()
    {
        env.SkipIfUnavailable();
        var lease = await env.AcquireAsync(env.PmCapability, "STATION-EXPIRED", ttlSeconds: 2);
        var leaseId = lease.GetProperty("leaseId").GetString()!;
        using var client = env.TestClient();

        await env.WaitForConditionAsync(async () =>
            (await client.GetAsync($"api/v1/leases/{leaseId}")).Content.ReadAsStringAsync().Result.Contains("Expired"),
            15, "lease did not expire");

        var response = await client.PostAsJsonAsync($"api/v1/leases/{leaseId}/power-meter/measure-power",
            new { clientId = "STATION-EXPIRED", channel = 1 });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("LEASE_EXPIRED",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()); // AC-08-05
    }
}

/// <summary>故障注入（NFR-03 / E2E-05 / E2E-06）：Adapter 失败 → Error；离线 → 退出候选。</summary>
public class ChaosTests(TestEnvironment env) : IClassFixture<TestEnvironment>
{
    private async Task<JsonElement> SetFaultsAsync(string resourceId, object faults)
    {
        using var admin = env.Admin();
        var response = await admin.PostAsJsonAsync($"api/v1/simulator/{resourceId}/faults", faults);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
    }

    [SkippableFact]
    public async Task FailSweep_ReturnsUnifiedError_WithInstrumentCode()
    {
        env.SkipIfUnavailable();
        var lease = await env.AcquireAsync(env.OsaCapability, "STATION-CHAOS-1");
        var leaseId = lease.GetProperty("leaseId").GetString()!;
        using var client = env.TestClient();

        await SetFaultsAsync(env.Osa1, new { failSweep = true });

        var response = await client.PostAsJsonAsync($"api/v1/leases/{leaseId}/osa/single-sweep", new { clientId = "STATION-CHAOS-1" });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ADAPTER_COMMAND_FAILED", body.GetProperty("code").GetString()); // 统一错误码
        Assert.Contains("SIM-OSA-SWEEP-ERR-42", body.GetProperty("detail").GetString()); // 保留仪器原始错误（AC-08-02）

        await SetFaultsAsync(env.Osa1, new { });
        await client.PostAsJsonAsync($"api/v1/leases/{leaseId}/release", new { clientId = "STATION-CHAOS-1", reason = "TestFailed" });
    }

    [SkippableFact]
    public async Task Offline_ExcludedFromCandidates()
    {
        env.SkipIfUnavailable();
        await SetFaultsAsync(env.Pm1, new { offline = true });

        // 健康检查 1s 周期、阈值 2 → 几秒内 Offline
        await env.WaitForConditionAsync(async () =>
            (await env.GetResourceAsync(env.Pm1)).GetProperty("connectivityState").GetString() == "Offline",
            15, "simulated offline was not detected");

        var discovery = await env.DiscoverAsync(env.PmCapability);
        var ids = discovery.GetProperty("candidates").EnumerateArray()
            .Select(c => c.GetProperty("resourceId").GetString()).ToList();
        Assert.DoesNotContain(env.Pm1, ids); // AC-07-01：Offline 退出候选

        await SetFaultsAsync(env.Pm1, new { });
        await env.WaitForConditionAsync(async () =>
            (await env.GetResourceAsync(env.Pm1)).GetProperty("connectivityState").GetString() == "Online",
            15, "simulated device did not come back online"); // AC-07-02
    }

    [SkippableFact]
    public async Task FailReset_OnExpiry_PutsResourceIntoError_AdminCanRecover()
    {
        env.SkipIfUnavailable();
        await SetFaultsAsync(env.Osa1, new { failReset = true });
        var lease = await env.AcquireAsync(env.OsaCapability, "STATION-CHAOS-2", ttlSeconds: 2);
        var leaseId = lease.GetProperty("leaseId").GetString()!;

        // 过期 → Recovery Reset 失败 → Error（AC-05-05）
        await env.WaitForConditionAsync(async () =>
            (await env.GetResourceAsync(env.Osa1)).GetProperty("operationalState").GetString() == "Error",
            15, "resource did not enter Error after failed reset");
        await env.WaitForConditionAsync(async () =>
            (await env.GetEventsAsync(leaseId)).EnumerateArray()
                .Any(e => e.GetProperty("eventType").GetString() == "LeaseExpired"),
            10, "LeaseExpired event missing");

        // 清除故障 → 管理员复位 → Available（AC-12-02）
        await SetFaultsAsync(env.Osa1, new { });
        using var admin = env.Admin();
        var reset = await admin.PostAsync($"api/v1/resources/{env.Osa1}/reset", null);
        reset.EnsureSuccessStatusCode();
        var resetBody = await reset.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Available", resetBody.GetProperty("operationalState").GetString());
    }
}
