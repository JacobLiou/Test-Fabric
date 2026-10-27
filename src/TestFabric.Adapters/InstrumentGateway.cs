using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Dapper;
using Microsoft.Extensions.Logging;
using TestFabric.Core.Domain;
using TestFabric.Core.Persistence;
using TestFabric.Core.Ports;

namespace TestFabric.Adapters;

/// <summary>
/// 仪器网关实现（Core 端口）：
///   1. 每个资源一条 FIFO 命令队列 —— 同一仪器的所有指令（业务测量、健康探测、Recovery Reset）
///      严格串行，消除 VISA 会话并发写入风险（评审 P2）；
///   2. 每条业务命令绑定租约的取消令牌 —— ForceRelease 先取消在途指令，Reset 排在其后（评审 P0-2）；
///   3. 健康探测带等待上限：队列忙于长测量时返回 Skipped（跳过 ≠ 失败，FR-07-05）。
/// </summary>
public sealed class InstrumentGateway(
    AdapterRegistry registry,
    IDbConnectionFactory db,
    ILogger<InstrumentGateway> logger) : IInstrumentGateway
{
    private readonly ConcurrentDictionary<string, ResourceWorker> _workers = new();
    private readonly ConcurrentDictionary<string, ConcurrentBag<CancellationTokenSource>> _leaseCts = new();

    public async Task<TResult> ExecuteAsync<TResult>(
        string resourceId, string? leaseId, Func<CancellationToken, Task<TResult>> command, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(resourceId, leaseId,
            async token => tcs.TrySetResult(await command(token)),
            ex => tcs.TrySetException(ex),
            () => tcs.TrySetCanceled(cancellationToken),
            cancellationToken);
        return await tcs.Task;
    }

    public async Task<GatewayOutcome> ResetAsync(string resourceId, CancellationToken ct)
    {
        try
        {
            var resource = await FetchResourceAsync(resourceId, ct);
            if (resource is null) return GatewayOutcome.Failed;
            var adapter = registry.Get(resource.AdapterId);
            await ExecuteAsync<object?>(resourceId, null,
                async token =>
                {
                    await adapter.ResetAsync(resource, token);
                    return null;
                }, ct);
            return GatewayOutcome.Succeeded;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Adapter reset failed for {ResourceId}", resourceId);
            return GatewayOutcome.Failed;
        }
    }

    public async Task<HealthProbeResult> ProbeHealthAsync(string resourceId, int waitMs, CancellationToken ct)
    {
        var resource = await FetchResourceAsync(resourceId, ct);
        if (resource is null) return HealthProbeResult.Unhealthy;
        var adapter = registry.Get(resource.AdapterId);

        var tcs = new TaskCompletionSource<HealthProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var postedAt = Stopwatch.GetTimestamp();

        Post(resourceId, null,
            async token =>
            {
                // 出队太晚（队列忙于长测量）→ 跳过而不是执行，更不是失败
                if (Stopwatch.GetElapsedTime(postedAt) > TimeSpan.FromMilliseconds(waitMs + 100))
                {
                    tcs.TrySetResult(HealthProbeResult.Skipped);
                    return;
                }
                var healthy = await adapter.HealthCheckAsync(resource, token);
                tcs.TrySetResult(healthy ? HealthProbeResult.Healthy : HealthProbeResult.Unhealthy);
            },
            ex =>
            {
                logger.LogWarning(ex, "L2 health probe errored for {ResourceId}", resourceId);
                tcs.TrySetResult(HealthProbeResult.Unhealthy);
            },
            () => tcs.TrySetResult(HealthProbeResult.Skipped),
            ct);

        var winner = await Task.WhenAny(tcs.Task, Task.Delay(waitMs, ct));
        return winner == tcs.Task ? await tcs.Task : HealthProbeResult.Skipped;
    }

    public void CancelCommands(string leaseId)
    {
        if (_leaseCts.TryGetValue(leaseId, out var sources))
        {
            foreach (var cts in sources)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }
        }
    }

    // ------------------------------------------------------------------

    private void Post(
        string resourceId, string? leaseId,
        Func<CancellationToken, Task> body,
        Action<Exception> onError,
        Action onCanceled,
        CancellationToken httpCancellationToken)
    {
        var worker = _workers.GetOrAdd(resourceId, _ => new ResourceWorker());
        var linked = CancellationTokenSource.CreateLinkedTokenSource(httpCancellationToken);
        if (leaseId is not null)
            _leaseCts.GetOrAdd(leaseId, _ => new ConcurrentBag<CancellationTokenSource>()).Add(linked);

        worker.Post(new WorkItem(leaseId, linked, body, onError, onCanceled));
    }

    private async Task<Resource?> FetchResourceAsync(string resourceId, CancellationToken ct)
    {
        await using var conn = await db.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Resource>(new CommandDefinition(
            "SELECT * FROM resources WHERE resource_id = @Id AND deleted_at IS NULL",
            new { Id = resourceId }, cancellationToken: ct));
    }

    private sealed record WorkItem(
        string? LeaseId,
        CancellationTokenSource Cts,
        Func<CancellationToken, Task> Body,
        Action<Exception> OnError,
        Action OnCanceled);

    /// <summary>每资源 FIFO 命令队列的消费者。队列本身保证同一仪器的指令严格串行。</summary>
    private sealed class ResourceWorker
    {
        private readonly Channel<WorkItem> _queue = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });

        public ResourceWorker()
        {
            _ = Task.Run(ConsumeAsync);
        }

        public void Post(WorkItem item) => _queue.Writer.TryWrite(item);

        private async Task ConsumeAsync()
        {
            await foreach (var item in _queue.Reader.ReadAllAsync())
            {
                try
                {
                    if (item.Cts.IsCancellationRequested)
                    {
                        item.OnCanceled();
                        continue;
                    }
                    await item.Body(item.Cts.Token);
                }
                catch (OperationCanceledException)
                {
                    item.OnCanceled();
                }
                catch (Exception ex)
                {
                    item.OnError(ex);
                }
                finally
                {
                    item.Cts.Dispose();
                }
            }
        }
    }
}
