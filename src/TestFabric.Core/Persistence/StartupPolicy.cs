using Dapper;
using Microsoft.Extensions.Logging;
using TestFabric.Contracts;
using TestFabric.Core.Domain;
using TestFabric.Core.Persistence;
using TestFabric.Core.Services;

namespace TestFabric.Core.Persistence;

/// <summary>
/// 启动宽限策略（评审 P0-1）：
/// 服务重启后，所有 Active Lease 的 TTL 从「恢复时刻」重新起算，
/// 防止重启期间丢失的心跳被误判为客户端崩溃，触发集中 Reset 风暴。
/// 前提：本服务重启时设备上的测量可能仍在进行（E2E-04 / FR-05-10）。
/// </summary>
public static class StartupPolicy
{
    public static async Task ApplyLeaseGraceAsync(
        IDbConnectionFactory db, AuditService audit, ILogger logger, bool enabled, CancellationToken ct)
    {
        if (!enabled)
        {
            logger.LogInformation("Startup lease grace disabled by configuration");
            return;
        }

        await using var conn = await db.CreateOpenConnectionAsync(ct);
        var count = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE leases SET expires_at = now() + ttl_seconds * interval '1 second'
            WHERE status = 'Active'
            """,
            cancellationToken: ct));

        if (count > 0)
        {
            logger.LogInformation("Startup grace applied to {Count} active lease(s): TTL clock restarted from recovery time", count);
            await audit.EmitAsync(new EventDraft(EventTypes.StartupGraceApplied,
                Payload: new() { ["activeLeases"] = count, ["action"] = "ttl-restarted-from-recovery-time" }), ct);
        }
    }
}
