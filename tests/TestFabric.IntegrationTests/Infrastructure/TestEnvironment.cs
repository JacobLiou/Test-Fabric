using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Testcontainers.PostgreSql;
using JsonElement = System.Text.Json.JsonElement;

namespace TestFabric.IntegrationTests.Infrastructure;

/// <summary>
/// 集成测试环境：
///   优先级 TESTFABRIC_TEST_DB 环境变量（直连现成 Postgres）→ Docker/Testcontainers（每类一个容器）→ 跳过。
/// 每个测试类通过唯一 Prefix 隔离能力与资源（共享数据库时也不串扰）。
/// </summary>
public sealed class TestEnvironment : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private PostgreSqlContainer? _container;
    private string _connectionString = "";

    public string Prefix { get; } = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    public string OsaCapability => $"OSA.{Prefix}";
    public string PmCapability => $"PM.{Prefix}";
    public TestFabricAppFactory App { get; private set; } = null!;
    public bool Available { get; private set; } = true;
    public string UnavailableReason { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var envDb = Environment.GetEnvironmentVariable("TESTFABRIC_TEST_DB");
        if (!string.IsNullOrEmpty(envDb))
        {
            _connectionString = envDb;
        }
        else if (DockerDetector.IsDockerAvailable())
        {
            try
            {
                _container = new PostgreSqlBuilder()
                    .WithImage("postgres:16-alpine")
                    .WithTmpfsMount("/var/lib/postgresql/data") // 测试库放内存，快
                    .Build();
                await _container.StartAsync();
                _connectionString = _container.GetConnectionString() + ";Pooling=false";
            }
            catch (Exception ex)
            {
                Available = false;
                UnavailableReason = $"Testcontainers failed: {ex.Message}";
                return;
            }
        }
        else
        {
            Available = false;
            UnavailableReason = "Docker not available and TESTFABRIC_TEST_DB not set";
            return;
        }

        App = new TestFabricAppFactory(_connectionString);
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        App?.Dispose();
        if (_container is not null) await _container.DisposeAsync();
    }

    public void SkipIfUnavailable()
    {
        if (!Available)
            throw new Xunit.SkipException($"Integration tests skipped: {UnavailableReason}");
    }

    // ---------------- 客户端 ----------------

    public HttpClient Admin() => Client("test-admin-token");
    public HttpClient Engineer() => Client("test-engineer-token");
    public HttpClient TestClient() => Client("test-client-token");
    public HttpClient Viewer() => Client("test-viewer-token");

    private HttpClient Client(string token)
    {
        var client = App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ---------------- 种子（经 API，顺带验证注册链路） ----------------

    private async Task SeedAsync()
    {
        var engineer = Engineer();
        await engineer.PostAsJsonAsync("api/v1/capabilities", new
        {
            capabilityId = OsaCapability,
            version = 1,
            schema = new { properties = new Dictionary<string, object> {
                ["wavelengthMinNm"] = new { type = "number" },
                ["wavelengthMaxNm"] = new { type = "number" },
                ["resolutionMinNm"] = new { type = "number" },
            } },
        });
        await engineer.PostAsJsonAsync("api/v1/capabilities", new
        {
            capabilityId = PmCapability,
            version = 1,
            schema = new { properties = new Dictionary<string, object> {
                ["channelCount"] = new { type = "number" },
            } },
        });

        await engineer.PostAsJsonAsync("api/v1/resources", Resource("OSA", "001", "OpticalSpectrumAnalyzer",
            "Yokogawa", "AQ6370D", OsaCapability, new Dictionary<string, object?>
            {
                ["wavelengthMinNm"] = 600, ["wavelengthMaxNm"] = 1700, ["resolutionMinNm"] = 0.02,
            }, calibrationDueDate: "2099-12-31"));
        await engineer.PostAsJsonAsync("api/v1/resources", Resource("OSA", "002", "OpticalSpectrumAnalyzer",
            "Yokogawa", "AQ6370D", OsaCapability, new Dictionary<string, object?>
            {
                ["wavelengthMinNm"] = 600, ["wavelengthMaxNm"] = 1700, ["resolutionMinNm"] = 0.1,  // AC-02-02：分辨率不达标
            }, calibrationDueDate: "2020-01-01")); // E2E-05：校准过期
        await engineer.PostAsJsonAsync("api/v1/resources", Resource("PM", "001", "OpticalPowerMeter",
            "Keysight", "N7744A", PmCapability, new Dictionary<string, object?> { ["channelCount"] = 4 }, "2099-12-31"));
        await engineer.PostAsJsonAsync("api/v1/resources", Resource("PM", "002", "OpticalPowerMeter",
            "Keysight", "N7744A", PmCapability, new Dictionary<string, object?> { ["channelCount"] = 4 }, "2099-12-31"));

        // 等待健康监控把资源置为 Available（测试配置 1s 周期）
        await WaitForConditionAsync(async () =>
        {
            var discovery = await DiscoverAsync(OsaCapability);
            return discovery.GetProperty("candidates").GetArrayLength() >= 2;
        }, timeoutSeconds: 20, "OSA resources did not become Available");
        await WaitForConditionAsync(async () =>
        {
            var discovery = await DiscoverAsync(PmCapability);
            return discovery.GetProperty("candidates").GetArrayLength() >= 2;
        }, timeoutSeconds: 20, "PM resources did not become Available");
    }

    public object Resource(string type, string index, string resourceType, string manufacturer, string model,
        string capability, Dictionary<string, object?> properties, string calibrationDueDate) => new
    {
        resourceId = $"{type}-{Prefix}-{index}",
        resourceType,
        manufacturer,
        model,
        addressType = "SIMULATOR",
        address = $"sim://{type}-{Prefix}-{index}",
        adapterId = type == "OSA" ? "sim.osa" : "sim.pm",
        location = "Lab-A/Rack-02",
        calibrationDueDate,
        capabilities = new[] { new { capabilityId = capability, properties } },
    };

    public string Osa1 => $"OSA-{Prefix}-001";
    public string Osa2 => $"OSA-{Prefix}-002";
    public string Pm1 => $"PM-{Prefix}-001";

    // ---------------- HTTP 辅助 ----------------

    public async Task<JsonElement> DiscoverAsync(string capability, Dictionary<string, JsonElement>? constraints = null)
    {
        using var client = TestClient();
        object body = constraints is null ? new { capability } : new { capability, constraints };
        var response = await client.PostAsJsonAsync("api/v1/resources/discover", body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> AcquireAsync(string capability, string clientId,
        Dictionary<string, JsonElement>? constraints = null, int ttlSeconds = 30, string? idempotencyKey = null,
        int waitTimeoutSeconds = 2)
    {
        using var client = TestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/leases/acquire")
        {
            Content = JsonContent.Create(new
            {
                clientId, capability,
                constraints = constraints as object,
                leaseTtlSeconds = ttlSeconds,
                waitTimeoutSeconds,
            }),
        };
        if (idempotencyKey is not null)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<HttpResponseMessage> AcquireShouldFailAsync(string capability, string clientId,
        Dictionary<string, JsonElement>? constraints = null, int waitTimeoutSeconds = 1)
    {
        using var client = TestClient();
        var response = await client.PostAsJsonAsync<object>("api/v1/leases/acquire", new
        {
            clientId, capability, constraints = constraints as object, waitTimeoutSeconds,
        });
        return response;
    }

    public async Task<JsonElement> GetResourceAsync(string resourceId)
    {
        using var client = Viewer();
        return await client.GetFromJsonAsync<JsonElement>($"api/v1/resources/{resourceId}");
    }

    public async Task<JsonElement> GetEventsAsync(string? resourceId = null)
    {
        using var client = Viewer();
        var url = string.IsNullOrEmpty(resourceId) ? "api/v1/events?limit=200" : $"api/v1/events?resourceId={resourceId}&limit=200";
        return await client.GetFromJsonAsync<JsonElement>(url);
    }

    public async Task WaitForConditionAsync(Func<Task<bool>> condition, int timeoutSeconds, string what)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            if (await condition()) return;
            await Task.Delay(200);
        }
        throw new TimeoutException(what);
    }

    /// <summary>不变量金丝雀（评审 P3 / E2E-02）：同一资源不得存在两个 Active Lease。</summary>
    public async Task AssertNoDoubleActiveLeaseAsync()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        var bad = await conn.QueryAsync<string>(
            "SELECT resource_id FROM leases WHERE status = 'Active' GROUP BY resource_id HAVING count(*) > 1");
        Assert.Empty(bad);
    }
}

public static class DockerDetector
{
    private static readonly Lazy<bool> Lazy = new(Detect);

    public static bool IsDockerAvailable() => Lazy.Value;

    private static bool Detect()
    {
        if (File.Exists(@"\\.\pipe\docker_engine") || File.Exists("/var/run/docker.sock"))
        {
            // 管道存在不代表守护进程在跑（Docker Desktop 未启动）
            try
            {
                var psi = new ProcessStartInfo("docker", "ps -q") { RedirectStandardOutput = true, RedirectStandardError = true };
                using var process = Process.Start(psi);
                if (process is null) return false;
                if (!process.WaitForExit(5000)) { try { process.Kill(); } catch { } return false; }
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
        return false;
    }
}
