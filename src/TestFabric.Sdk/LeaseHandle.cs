namespace TestFabric.Sdk;

/// <summary>
/// 租约句柄：自动心跳 + LeaseLost 回调 + 释放。
/// 设计要点（评审 P0-3）：
///   - 心跳由后台任务自动发送（默认 TTL/3，钳制在 [2s, 30s]）；
///   - 服务端时间为权威（HeartbeatResponse.ServerTime / ExpiresAt），SDK 不依赖本地时钟推断过期；
///   - LeaseLost 触发时（服务端判过期/被强制释放），业务代码必须中止测试且不得写入结果。
/// </summary>
public sealed class LeaseHandle : IAsyncDisposable
{
    private readonly TestFabricClient _client;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _heartbeat;

    public string LeaseId { get; }
    public string ResourceId { get; }
    public string Capability { get; }
    public DateTimeOffset ExpiresAt { get; internal set; }
    public bool IsLost { get; private set; }
    public bool IsReleased { get; private set; }

    /// <summary>租约丢失（服务端过期/强制释放/所有者不匹配）。参数为错误码。</summary>
    public event Action<LeaseHandle, string>? LeaseLost;

    internal LeaseHandle(TestFabricClient client, Contracts.AcquireResponse response)
    {
        _client = client;
        LeaseId = response.LeaseId;
        ResourceId = response.ResourceId;
        Capability = response.Capability;
        ExpiresAt = response.ExpiresAt;
        _heartbeat = Task.Run(() => HeartbeatLoopAsync(response.TtlSeconds, _cts.Token));
    }

    /// <summary>心跳间隔：TTL/3，钳制 [2s, 30s]。公开以便单测。</summary>
    public static TimeSpan ComputeHeartbeatInterval(int ttlSeconds)
        => TimeSpan.FromSeconds(Math.Clamp(ttlSeconds / 3.0, 2, 30));

    private async Task HeartbeatLoopAsync(int ttlSeconds, CancellationToken ct)
    {
        var interval = ComputeHeartbeatInterval(ttlSeconds);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                var resp = await _client.HeartbeatAsync(LeaseId, ct);
                ExpiresAt = resp.ExpiresAt;
            }
            catch (FabricSdkException ex) when (ex.Code is "LEASE_EXPIRED" or "LEASE_OWNER_MISMATCH" or "LEASE_NOT_FOUND")
            {
                IsLost = true;
                try { LeaseLost?.Invoke(this, ex.Code); } catch { }
                return;
            }
            catch (Exception)
            {
                // 瞬时网络抖动：下一轮重试（NFR-02：短暂抖动不得立即判定丢失）
            }
        }
    }

    public async Task<Contracts.ReleaseResponse> ReleaseAsync(string reason = "Completed", CancellationToken ct = default)
    {
        if (IsReleased) throw new FabricSdkException("ALREADY_RELEASED", $"lease {LeaseId} already released");
        IsReleased = true;
        await _cts.CancelAsync();
        try { await _heartbeat; } catch (OperationCanceledException) { }
        return await _client.ReleaseAsync(LeaseId, reason, ct);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!IsReleased && !IsLost)
                await ReleaseAsync("Completed");
            else
            {
                _cts.Cancel();
                try { await _heartbeat; } catch (OperationCanceledException) { }
            }
        }
        catch (Exception)
        {
            // Dispose 中不抛出；释放失败由过期扫描兜底
        }
        finally { _cts.Dispose(); }
    }
}
