using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TestFabric.IntegrationTests.Infrastructure;
using JsonElement = System.Text.Json.JsonElement;

namespace TestFabric.IntegrationTests;

/// <summary>FR-04/05/06 租约生命周期验收（含过期恢复，AC-05-03/04）。</summary>
public class LeaseLifecycleTests(TestEnvironment env) : IClassFixture<TestEnvironment>
{
    [SkippableFact]
    public async Task Acquire_ThenHeartbeat_ExtendsExpiry()
    {
        env.SkipIfUnavailable();
        var lease = await env.AcquireAsync(env.OsaCapability, "STATION-HB", ttlSeconds: 30);
        var leaseId = lease.GetProperty("leaseId").GetString()!;
        Assert.Equal("Active", lease.GetProperty("status").GetString());
        Assert.Equal(env.Osa1, lease.GetProperty("resourceId").GetString());

        using var client = env.TestClient();
        var before = lease.GetProperty("expiresAt").GetDateTimeOffset();
        await Task.Delay(1200);
        var hb = await client.PostAsJsonAsync($"api/v1/leases/{leaseId}/heartbeat", new { clientId = "STATION-HB" });
        hb.EnsureSuccessStatusCode();
        var after = (await hb.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("expiresAt").GetDateTimeOffset();
        Assert.True(after > before); // AC-05-02：心跳续期

        var resource = await env.GetResourceAsync(env.Osa1);
        Assert.Equal("Busy", resource.GetProperty("operationalState").GetString());
        Assert.Equal("STATION-HB", resource.GetProperty("currentLease").GetProperty("clientId").GetString()); // AC-09-02

        var release = await client.PostAsJsonAsync($"api/v1/leases/{leaseId}/release", new { clientId = "STATION-HB", reason = "Completed" });
        release.EnsureSuccessStatusCode();
    }

    [SkippableFact]
    public async Task Release_IsIdempotent_And_OwnerChecked()
    {
        env.SkipIfUnavailable();
        var lease = await env.AcquireAsync(env.OsaCapability, "STATION-REL");
        var leaseId = lease.GetProperty("leaseId").GetString()!;
        using var client = env.TestClient();

        // 非 Owner 释放 → 403（AC-06-03）
        var stranger = await client.PostAsJsonAsync($"api/v1/leases/{leaseId}/release", new { clientId = "STRANGER", reason = "Completed" });
        Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);

        var first = await client.PostAsJsonAsync($"api/v1/leases/{leaseId}/release", new { clientId = "STATION-REL", reason = "Completed" });
        first.EnsureSuccessStatusCode();
        Assert.Equal("Released", (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString());

        var second = await client.PostAsJsonAsync($"api/v1/leases/{leaseId}/release", new { clientId = "STATION-REL", reason = "Completed" });
        second.EnsureSuccessStatusCode();
        Assert.Equal("AlreadyReleased", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString()); // AC-06-02

        await env.WaitForConditionAsync(async () =>
            (await env.GetResourceAsync(env.Osa1)).GetProperty("operationalState").GetString() == "Available",
            10, "resource did not return to Available after release");
    }

    [SkippableFact]
    public async Task IdempotentAcquire_ReturnsSameLease_NoDoubleOccupancy()
    {
        env.SkipIfUnavailable();
        var key = $"STATION-IDEM-{env.Prefix}";
        var first = await env.AcquireAsync(env.OsaCapability, "STATION-IDEM", idempotencyKey: key);
        var second = await env.AcquireAsync(env.OsaCapability, "STATION-IDEM", idempotencyKey: key);

        var firstId = first.GetProperty("leaseId").GetString();
        Assert.Equal(firstId, second.GetProperty("leaseId").GetString()); // AC-04-03
        Assert.True(second.GetProperty("idempotentReplay").GetBoolean());

        // 同 key 不同请求体 → 409
        var conflict = await env.AcquireShouldFailAsync(env.OsaCapability, "STATION-IDEM");
        using var client = env.TestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/leases/acquire")
        {
            Content = JsonContent.Create(new { clientId = "STATION-IDEM", capability = env.PmCapability }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_CONFLICT",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        await client.PostAsJsonAsync($"api/v1/leases/{firstId}/release", new { clientId = "STATION-IDEM", reason = "Completed" });
    }

    [SkippableFact]
    public async Task ExpiredLease_TriggersRecovery_ResourceReturnsToPool()
    {
        env.SkipIfUnavailable();
        var lease = await env.AcquireAsync(env.OsaCapability, "STATION-DIE", ttlSeconds: 2); // 最小 TTL
        var leaseId = lease.GetProperty("leaseId").GetString()!;
        using var client = env.TestClient();

        // 不发心跳 → TTL 后过期（扫描器 300ms 周期）
        await env.WaitForConditionAsync(async () =>
        {
            var response = await client.GetAsync($"api/v1/leases/{leaseId}");
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return body.GetProperty("status").GetString() == "Expired";
        }, 15, "lease did not expire"); // AC-05-03

        await env.WaitForConditionAsync(async () =>
            (await env.GetResourceAsync(env.Osa1)).GetProperty("operationalState").GetString() == "Available",
            15, "resource did not recover to Available"); // AC-05-04（Simulator Reset 成功）

        // 完整事件链（AC-10-01 / E2E-03）
        var events = await env.GetEventsAsync(leaseId);
        var types = events.EnumerateArray().Select(e => e.GetProperty("eventType").GetString()).ToList();
        Assert.Contains("HeartbeatLost", types);
        Assert.Contains("LeaseExpired", types);

        // 之后可重新分配
        var reAcquired = await env.AcquireAsync(env.OsaCapability, "STATION-REBORN");
        Assert.Equal(env.Osa1, reAcquired.GetProperty("resourceId").GetString());
        await client.PostAsJsonAsync($"api/v1/leases/{reAcquired.GetProperty("leaseId").GetString()}/release",
            new { clientId = "STATION-REBORN", reason = "Completed" });
    }

    [SkippableFact]
    public async Task ForceRelease_RequiresReason_And_Audited()
    {
        env.SkipIfUnavailable();
        var lease = await env.AcquireAsync(env.PmCapability, "STATION-FR");
        var leaseId = lease.GetProperty("leaseId").GetString()!;
        using var admin = env.Admin();
        using var client = env.TestClient();

        var noReason = await admin.PostAsJsonAsync($"api/v1/leases/{leaseId}/force-release", new { });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode); // AC-12-04
        Assert.Equal("REASON_REQUIRED", (await noReason.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var forced = await admin.PostAsJsonAsync($"api/v1/leases/{leaseId}/force-release", new { reason = "soak-test-cleanup" });
        forced.EnsureSuccessStatusCode();

        var events = await env.GetEventsAsync(leaseId);
        Assert.Contains(events.EnumerateArray(), e => e.GetProperty("eventType").GetString() == "AdminOverride"); // AC-11-03

        await env.WaitForConditionAsync(async () =>
        {
            var response = await client.GetAsync($"api/v1/leases/{leaseId}");
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return body.GetProperty("status").GetString() == "Closed";
        }, 10, "force-released lease not closed");
    }
}

/// <summary>E2E-02 并发互斥：同一时刻最多一个 Active Lease；幂等键并发也只有一个租约。</summary>
public class ConcurrencyTests(TestEnvironment env) : IClassFixture<TestEnvironment>
{
    [SkippableFact]
    public async Task TenConcurrentAcquires_SingleResource_ExactlyOneWins()
    {
        env.SkipIfUnavailable();
        // 用分辨率约束把候选收窄到 OSA-001（OSA-002 分辨率 0.1 不满足）
        var constraints = new Dictionary<string, JsonElement>
        {
            ["resolutionMinNm"] = JsonSerializer.Deserialize<JsonElement>(@"{""operator"":""lte"",""value"":0.05}"),
        };

        var tasks = Enumerable.Range(0, 10)
            .Select(i => env.AcquireAsync(env.OsaCapability, $"STATION-C{i}", constraints, waitTimeoutSeconds: 0))
            .ToList();

        var responses = await Task.WhenAll(
            tasks.Select(async t =>
            {
                try { return (won: true, await t); }
                catch { return (won: false, (JsonElement?)null); }
            }));

        // Assert.WhenAll 已经在 env.AcquireAsync 内 EnsureSuccess —— 并发失败路径走 AcquireShouldFail
        var winners = responses.Where(r => r.won).ToList();
        Assert.Single(winners); // AC-04-02 / AC-05-07

        await env.AssertNoDoubleActiveLeaseAsync(); // E2E-02 不变量
    }

    [SkippableFact]
    public async Task TenConcurrentAcquires_SingleResource_LosersGetNoResourceAvailable()
    {
        env.SkipIfUnavailable();
        var constraints = new Dictionary<string, JsonElement>
        {
            ["resolutionMinNm"] = JsonSerializer.Deserialize<JsonElement>(@"{""operator"":""lte"",""value"":0.05}"),
        };

        var tasks = Enumerable.Range(0, 10)
            .Select(i => env.AcquireShouldFailAsync(env.OsaCapability, $"STATION-L{i}", constraints, waitTimeoutSeconds: 0))
            .ToList();
        var responses = await Task.WhenAll(tasks);

        var success = responses.Count(r => r.IsSuccessStatusCode);
        var noResource = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Conflict &&
            r.Content.ReadAsStringAsync().Result.Contains("NO_RESOURCE_AVAILABLE"));

        Assert.Equal(1, success);
        Assert.Equal(9, noResource); // AC-04-02：失败方得到明确错误码
        foreach (var response in responses) response.Dispose();

        // 释放后可再次获得（E2E-02 步骤 5）
        using var client = env.TestClient();
        var leases = await client.GetFromJsonAsync<JsonElement>("api/v1/leases?status=Active&limit=10");
        var active = leases!.EnumerateArray().ToList();
        Assert.Single(active);
        var leaseId = active[0].GetProperty("leaseId").GetString();
        var ownerId = active[0].GetProperty("clientId").GetString();
        await client.PostAsJsonAsync($"api/v1/leases/{leaseId}/release", new { clientId = ownerId, reason = "Completed" });

        var reAcquired = await env.AcquireAsync(env.OsaCapability, "STATION-AFTER", constraints);
        Assert.Equal(env.Osa1, reAcquired.GetProperty("resourceId").GetString());
        await client.PostAsJsonAsync($"api/v1/leases/{reAcquired.GetProperty("leaseId").GetString()}/release",
            new { clientId = "STATION-AFTER", reason = "Completed" });
    }

    [SkippableFact]
    public async Task ConcurrentAcquires_SameIdempotencyKey_AllGetSameLease()
    {
        env.SkipIfUnavailable();
        var key = $"CONCURRENT-IDEM-{env.Prefix}";
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => env.AcquireAsync(env.PmCapability, "STATION-SAME", idempotencyKey: key, waitTimeoutSeconds: 0))
            .ToList();

        var results = await Task.WhenAll(tasks);
        var leaseIds = results.Select(r => r.GetProperty("leaseId").GetString()).Distinct().ToList();
        Assert.Single(leaseIds); // 并发幂等：同一 key 只产生一个租约

        await env.AssertNoDoubleActiveLeaseAsync();
        using var client = env.TestClient();
        await client.PostAsJsonAsync($"api/v1/leases/{leaseIds[0]}/release", new { clientId = "STATION-SAME", reason = "Completed" });
    }
}
