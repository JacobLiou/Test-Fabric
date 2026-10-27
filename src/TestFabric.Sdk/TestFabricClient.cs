using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TestFabric.Sdk;

/// <summary>
/// Test Fabric 客户端 SDK（评审 P0-3：SDK 是一等公民）。
/// 心跳由 LeaseHandle 后台自动发送；业务代码不写 setInterval。
/// 用法：
///   await using var lease = await client.AcquireAsync(req, idempotencyKey);
///   lease.LeaseLost += (l, code) => AbortTestAndDontSaveResults();
///   var sweep = await client.OsaSingleSweepAsync(lease.LeaseId);
///   await lease.ReleaseAsync("Completed");
/// </summary>
public sealed class TestFabricClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    public string ClientId { get; }

    public TestFabricClient(Uri baseUri, string apiToken, string clientId)
    {
        ClientId = clientId;
        _http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
    }

    // ---------------- Discovery / Acquire ----------------

    public Task<Contracts.DiscoverResponse> DiscoverAsync(
        string capability, Dictionary<string, JsonElement>? constraints = null, CancellationToken ct = default)
        => PostAsync<Contracts.DiscoverResponse>("api/v1/resources/discover",
            new Contracts.DiscoverRequest(capability, constraints), ct);

    public async Task<LeaseHandle> AcquireAsync(
        Contracts.AcquireRequest request, string? idempotencyKey = null, CancellationToken ct = default)
    {
        if (request.ClientId is null) request = request with { ClientId = ClientId };
        var msg = new HttpRequestMessage(HttpMethod.Post, "api/v1/leases/acquire")
        {
            Content = JsonContent.Create(request, options: Json),
        };
        if (idempotencyKey is not null)
            msg.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        var response = await SendAsync<Contracts.AcquireResponse>(msg, ct);
        return new LeaseHandle(this, response);
    }

    // ---------------- Execute（经资源串行队列） ----------------

    public Task<Contracts.OsaConfigureResult> OsaConfigureAsync(string leaseId, double startNm, double stopNm, double resolutionNm, CancellationToken ct = default)
        => PostAsync<Contracts.OsaConfigureResult>($"api/v1/leases/{leaseId}/osa/configure",
            new Contracts.OsaConfigureCommand(ClientId, startNm, stopNm, resolutionNm), ct);

    public Task<Contracts.OsaSweepResult> OsaSingleSweepAsync(string leaseId, CancellationToken ct = default)
        => PostAsync<Contracts.OsaSweepResult>($"api/v1/leases/{leaseId}/osa/single-sweep",
            new Contracts.OsaSweepCommand(ClientId), ct);

    public Task<Contracts.OsaTrace> OsaGetTraceAsync(string leaseId, string sweepId, CancellationToken ct = default)
        => PostAsync<Contracts.OsaTrace>($"api/v1/leases/{leaseId}/osa/get-trace",
            new Contracts.OsaTraceCommand(ClientId, sweepId), ct);

    public Task<Contracts.OsaPeak> OsaGetPeakAsync(string leaseId, string sweepId, CancellationToken ct = default)
        => PostAsync<Contracts.OsaPeak>($"api/v1/leases/{leaseId}/osa/get-peak",
            new Contracts.OsaPeakCommand(ClientId, sweepId), ct);

    public Task<Contracts.PmPowerResult> PmMeasurePowerAsync(string leaseId, int channel, double? wavelengthNm = null, CancellationToken ct = default)
        => PostAsync<Contracts.PmPowerResult>($"api/v1/leases/{leaseId}/power-meter/measure-power",
            new Contracts.PmMeasureCommand(ClientId, channel, wavelengthNm), ct);

    public Task<Contracts.PmZeroResult> PmZeroAsync(string leaseId, int channel, CancellationToken ct = default)
        => PostAsync<Contracts.PmZeroResult>($"api/v1/leases/{leaseId}/power-meter/zero",
            new Contracts.PmZeroCommand(ClientId, channel), ct);

    // ---------------- 查询 ----------------

    public Task<Contracts.ResourceDto> GetResourceAsync(string resourceId, CancellationToken ct = default)
        => GetAsync<Contracts.ResourceDto>($"api/v1/resources/{resourceId}", ct);

    internal Task<Contracts.HeartbeatResponse> HeartbeatAsync(string leaseId, CancellationToken ct)
        => PostAsync<Contracts.HeartbeatResponse>($"api/v1/leases/{leaseId}/heartbeat",
            new Contracts.HeartbeatRequest(ClientId), ct);

    internal Task<Contracts.ReleaseResponse> ReleaseAsync(string leaseId, string reason, CancellationToken ct)
        => PostAsync<Contracts.ReleaseResponse>($"api/v1/leases/{leaseId}/release",
            new Contracts.ReleaseRequest(ClientId, reason), ct);

    // ---------------- HTTP 基础设施 ----------------

    private async Task<T> PostAsync<T>(string url, object body, CancellationToken ct)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: Json) };
        return await SendAsync<T>(msg, ct);
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
        => await SendAsync<T>(new HttpRequestMessage(HttpMethod.Get, url), ct);

    private async Task<T> SendAsync<T>(HttpRequestMessage msg, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(msg, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new FabricSdkException("TRANSPORT_ERROR", $"cannot reach Test Fabric: {ex.Message}");
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            string code = $"HTTP_{(int)resp.StatusCode}";
            string detail = body;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("code", out var codeEl)) code = codeEl.GetString() ?? code;
                if (doc.RootElement.TryGetProperty("detail", out var detailEl)) detail = detailEl.GetString() ?? detail;
            }
            catch (JsonException) { /* 非 problem+json 响应 */ }
            throw new FabricSdkException(code, detail, (int)resp.StatusCode);
        }
        return JsonSerializer.Deserialize<T>(body, Json) ?? throw new FabricSdkException("DESERIALIZE_FAILED", $"empty or invalid response for {msg.RequestUri}");
    }

    public void Dispose() => _http.Dispose();
}
