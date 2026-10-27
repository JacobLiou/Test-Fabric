using System.Net.Sockets;
using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TestFabric.Contracts;
using TestFabric.Core.Domain;
using TestFabric.Core.Options;
using TestFabric.Core.Persistence;
using TestFabric.Core.Ports;
using TestFabric.Core.Services;

namespace TestFabric.Core.Background;

/// <summary>
/// 健康监控（FR-07）：
///   L1 = TCP 连通（永远安全，Busy 时也执行）；
///   L2 = Adapter 基础查询（经资源串行队列，队列忙则跳过——跳过 ≠ 失败）；
///   连续 3 次失败 → Offline；连续 2 次成功 → Online（AC-07-01/02）；
///   Busy 资源绝不发送 Reset 类指令，也不干扰在途测量（AC-07-03）；
///   首次健康检查通过后 Unknown → Available（自动进入可分配池）。
/// </summary>
public sealed class HealthMonitorService(
    IServiceScopeFactory scopes,
    IDbConnectionFactory db,
    IInstrumentGateway gateway,
    TimeProvider time,
    Microsoft.Extensions.Options.IOptions<TestFabricOptions> options,
    ILogger<HealthMonitorService> logger) : BackgroundService
{
    private sealed record Counters(int Fails, int Successes);

    private readonly ConcurrentDictionary<string, Counters> _counters = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.Health.IntervalSeconds));
        logger.LogInformation("Health monitor started, interval {Interval}", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var registry = scope.ServiceProvider.GetRequiredService<RegistryService>();
                var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
                var resources = await registry.ListForHealthCheckAsync(stoppingToken);
                await Task.WhenAll(resources.Select(r => ProbeOneAsync(r, audit, stoppingToken)));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Health monitor cycle failed; retrying next interval");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        logger.LogInformation("Health monitor stopped");
    }

    private async Task ProbeOneAsync(Resource resource, AuditService audit, CancellationToken ct)
    {
        var health = options.Value.Health;
        var l1Ok = await L1TcpProbeAsync(resource, health.L1TimeoutMs, ct);

        if (!l1Ok)
        {
            var c = Bump(resource.ResourceId, fail: true);
            if (c.Fails >= health.FailThreshold && resource.ConnectivityState != States.Connectivity.Offline)
                await SetConnectivityAsync(resource, States.Connectivity.Offline, "L1 tcp probe failed", audit, ct);
            return;
        }

        var counters = Bump(resource.ResourceId, fail: false);
        var wasOffline = resource.ConnectivityState == States.Connectivity.Offline;
        if (wasOffline && counters.Successes < health.SuccessThreshold) return; // 需连续成功才恢复 Online（AC-07-02）

        if (resource.ConnectivityState != States.Connectivity.Online)
            await SetConnectivityAsync(resource, States.Connectivity.Online, "L1 tcp probe succeeded", audit, ct);

        // L2 只在非 Busy 时执行；队列忙 → Skipped（保持原状态，不计失败）
        if (resource.OperationalState == States.Operational.Busy) return;

        var probe = await gateway.ProbeHealthAsync(resource.ResourceId, health.ProbeWaitMs, ct);
        if (probe == HealthProbeResult.Skipped) return;

        var healthy = probe == HealthProbeResult.Healthy;
        var targetHealth = healthy ? States.Health.Healthy : States.Health.Degraded;
        if (resource.HealthState != targetHealth)
        {
            await ExecuteAsync(
                "UPDATE resources SET health_state = @H, updated_at = now() WHERE resource_id = @Id AND deleted_at IS NULL",
                new { H = targetHealth, resource.ResourceId }, ct);
            await audit.EmitAsync(new EventDraft(EventTypes.HealthChanged, resource.ResourceId,
                Payload: new() { ["from"] = resource.HealthState, ["to"] = targetHealth, ["l2"] = probe.ToString() }), ct);
        }

        // 首次健康检查通过 → 进入可分配池
        if (healthy && resource.OperationalState == States.Operational.Unknown)
        {
            await ExecuteAsync(
                "UPDATE resources SET operational_state = 'Available', updated_at = now() WHERE resource_id = @Id AND operational_state = 'Unknown'",
                new { resource.ResourceId }, ct);
            await audit.EmitAsync(new EventDraft(EventTypes.ResourceStateChanged, resource.ResourceId,
                Payload: new() { ["from"] = States.Operational.Unknown, ["to"] = States.Operational.Available, ["cause"] = "first-successful-health-check" }), ct);
        }
    }

    private Counters Bump(string resourceId, bool fail)
    {
        var current = _counters.GetOrAdd(resourceId, _ => new Counters(0, 0));
        var next = fail
            ? new Counters(current.Fails + 1, 0)
            : new Counters(0, current.Successes + 1);
        _counters[resourceId] = next;
        return next;
    }

    private async Task SetConnectivityAsync(Resource resource, string target, string cause, AuditService audit, CancellationToken ct)
    {
        await ExecuteAsync(
            "UPDATE resources SET connectivity_state = @C, updated_at = now() WHERE resource_id = @Id",
            new { C = target, resource.ResourceId }, ct);
        await audit.EmitAsync(new EventDraft(EventTypes.ConnectivityChanged, resource.ResourceId,
            Payload: new() { ["from"] = resource.ConnectivityState, ["to"] = target, ["cause"] = cause }), ct);
        logger.LogInformation("Resource {ResourceId} connectivity {From} -> {To} ({Cause})",
            resource.ResourceId, resource.ConnectivityState, target, cause);

        if (target == States.Connectivity.Offline)
        {
            // Offline 同时把健康状态置回 Unknown（连不上时健康未知）
            await ExecuteAsync(
                "UPDATE resources SET health_state = 'Unknown' WHERE resource_id = @Id AND connectivity_state = 'Offline'",
                new { resource.ResourceId }, ct);
        }
    }

    private async Task ExecuteAsync(string sql, object param, CancellationToken ct)
    {
        await using var conn = await db.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(new Dapper.CommandDefinition(sql, param, cancellationToken: ct));
    }

    /// <summary>L1：纯 TCP 连接（SIMULATOR 类型读故障开关）。</summary>
    private async Task<bool> L1TcpProbeAsync(Resource resource, int timeoutMs, CancellationToken ct)
    {
        if (resource.AddressType.Equals("SIMULATOR", StringComparison.OrdinalIgnoreCase))
            return true; // 模拟器离线故障由 L2 / Fault 开关控制（SimulatorFaults）

        try
        {
            var (host, port) = ParseHostPort(resource.Address);
            using var tcp = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
            await tcp.ConnectAsync(host, port, timeoutCts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or TimeoutException)
        {
            return false;
        }
    }

    internal static (string Host, int Port) ParseHostPort(string address)
    {
        var idx = address.LastIndexOf(':');
        if (idx >= 0 && int.TryParse(address[(idx + 1)..], out var port))
            return (address[..idx], port);
        return (address, 10001); // 常见仪器 raw TCP 端口；可在 address 中显式 host:port 覆盖
    }
}
