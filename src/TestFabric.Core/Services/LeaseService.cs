using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using TestFabric.Contracts;
using TestFabric.Core.Domain;
using TestFabric.Core.Events;
using TestFabric.Core.Matching;
using TestFabric.Core.Options;
using TestFabric.Core.Persistence;
using TestFabric.Core.Ports;

namespace TestFabric.Core.Services;

/// <summary>
/// 租约服务（FR-04 / FR-05 / FR-06 / FR-12）—— 平台并发正确性的核心。
/// 关键设计：
///   1. 匹配与加锁原子化：条件 UPDATE（operational_state='Available' → 'Busy'）+ Active Lease 部分唯一索引双保险；
///   2. 心跳续期、过期、释放全部以数据库行状态为权威，条件更新保证 Release/Expire/ForceRelease 互斥；
///   3. Recovery（Adapter Reset）在资源专属串行队列上排队，自然等待在途指令完成（评审 P0-2）；
///   4. 幂等键全局唯一，重放返回原租约（AC-04-03）。
/// </summary>
public sealed class LeaseService(
    IDbConnectionFactory db,
    RegistryService registry,
    CapabilityService capabilities,
    AuditService audit,
    IInstrumentGateway gateway,
    TimeProvider time,
    IOptions<TestFabricOptions> options)
{
    public async Task<AcquireResponse> AcquireAsync(AcquireRequest req, string? idempotencyKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ClientId))
            throw FabricException.Validation("clientId is required (use a run-instance identity, not just the station id)");

        var schema = await capabilities.GetRequiredSchemaAsync(req.Capability, ct);
        var constraints = ConstraintParser.Parse(req.Constraints);
        ConstraintParser.EnsureDefined(schema, constraints); // AC-02-04

        var ttl = Math.Clamp(req.LeaseTtlSeconds ?? options.Value.Lease.DefaultTtlSeconds,
            options.Value.Lease.MinTtlSeconds, options.Value.Lease.MaxTtlSeconds);
        var fingerprint = ComputeRequestHash(req, constraints);

        // 幂等检查（AC-04-03）
        if (idempotencyKey is not null)
        {
            var existing = await FindByIdempotencyKeyAsync(idempotencyKey, ct);
            if (existing is not null)
            {
                EnsureSameFingerprint(existing, fingerprint);
                return ToResponse(existing, replay: true);
            }
        }

        var waitMs = (req.WaitTimeoutSeconds ?? options.Value.Acquire.DefaultWaitTimeoutSeconds) * 1000L;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var latestExclusions = new List<ExcludedResource>();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var pool = await registry.LoadPoolAsync(req.Capability, ct);
            var outcome = ResourceMatcher.Evaluate(pool, constraints, time);
            latestExclusions = outcome.Excluded.ToList();

            foreach (var candidate in outcome.Candidates)
            {
                var granted = await TryCommitLeaseAsync(candidate, req, ttl, idempotencyKey, fingerprint, ct);
                if (granted is not null) return granted;
            }

            if (sw.ElapsedMilliseconds >= waitMs) break;
            await Task.Delay(options.Value.Acquire.PollIntervalMs, ct);
        }

        var reasonSummary = string.Join("; ", latestExclusions);
        await audit.EmitAsync(new EventDraft(EventTypes.AcquireFailed, ClientId: req.ClientId,
            Payload: new()
            {
                ["capability"] = req.Capability,
                ["constraints"] = constraints.ToDictionary(kv => kv.Key, kv => (object?)$"{kv.Value.Operator} {kv.Value.Value}"),
                ["waitedMs"] = sw.ElapsedMilliseconds,
                ["excluded"] = reasonSummary,
            }), ct); // AC-10-02

        throw new FabricException(ErrorCodes.NoResourceAvailable,
            $"no resource satisfies '{req.Capability}' within {waitMs} ms; excluded: {reasonSummary}",
            new() { ["waitedMs"] = sw.ElapsedMilliseconds });
    }

    /// <summary>单个候选的原子提交：条件 UPDATE + INSERT，靠部分唯一索引拦截并发竞争。</summary>
    private async Task<AcquireResponse?> TryCommitLeaseAsync(
        Resource candidate, AcquireRequest req, int ttl, string? idempotencyKey, string fingerprint, CancellationToken ct)
    {
        var lease = new Lease
        {
            LeaseId = NewLeaseId(),
            ResourceId = candidate.ResourceId,
            CapabilityId = req.Capability,
            ClientId = req.ClientId,
            IdempotencyKey = idempotencyKey,
            RequestHash = fingerprint,
            WorkOrder = req.Context?.WorkOrder ?? "",
            SerialNumber = req.Context?.SerialNumber ?? "",
            Operation = req.Context?.Operation ?? "",
            Status = States.LeaseStatus.Active,
            TtlSeconds = ttl,
            ExpiresAt = time.GetUtcNow().UtcDateTime.AddSeconds(ttl),
        };

        await using var conn = await db.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 原子检查（AC-04-06）：状态自 Available → Busy 的条件更新，rowcount=0 表示候选已被抢占
        var updated = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE resources SET operational_state = 'Busy', updated_at = now()
            WHERE resource_id = @ResourceId
              AND operational_state = 'Available'
              AND administrative_state = 'Enabled'
              AND connectivity_state = 'Online'
              AND health_state = 'Healthy'
              AND deleted_at IS NULL
            """,
            new { candidate.ResourceId }, tx, cancellationToken: ct));
        if (updated == 0)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO leases (lease_id, resource_id, capability_id, client_id, idempotency_key, request_hash,
                    work_order, serial_number, operation, status, ttl_seconds, acquired_at, expires_at)
                VALUES (@LeaseId, @ResourceId, @CapabilityId, @ClientId, @IdempotencyKey, @RequestHash,
                    @WorkOrder, @SerialNumber, @Operation, @Status, @TtlSeconds, now(), @ExpiresAt)
                """,
                new
                {
                    lease.LeaseId, lease.ResourceId, lease.CapabilityId, lease.ClientId,
                    lease.IdempotencyKey, lease.RequestHash, lease.WorkOrder, lease.SerialNumber,
                    lease.Operation, lease.Status, lease.TtlSeconds, lease.ExpiresAt,
                }, tx, cancellationToken: ct));

            var evt = await audit.AppendAsync(new EventDraft(EventTypes.AcquireSucceeded,
                lease.ResourceId, lease.LeaseId, req.ClientId,
                Payload: new()
                {
                    ["capability"] = req.Capability,
                    ["ttlSeconds"] = ttl,
                    ["workOrder"] = lease.WorkOrder,
                    ["serialNumber"] = lease.SerialNumber,
                    ["operation"] = lease.Operation,
                    ["idempotencyKey"] = idempotencyKey,
                }), conn, tx, ct);

            await tx.CommitAsync(ct);
            audit.Publish(evt);
            return ToResponse(lease, replay: false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await tx.RollbackAsync(ct);
            // 可能是并发竞争，也可能是同幂等键并发插入
            if (idempotencyKey is not null)
            {
                var existing = await FindByIdempotencyKeyAsync(idempotencyKey, ct);
                if (existing is not null)
                {
                    EnsureSameFingerprint(existing, fingerprint);
                    return ToResponse(existing, replay: true);
                }
            }
            return null;
        }
    }

    public async Task<HeartbeatResponse> HeartbeatAsync(string leaseId, string clientId, CancellationToken ct)
    {
        await using var conn = await db.CreateOpenConnectionAsync(ct);
        var updated = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE leases SET expires_at = now() + ttl_seconds * interval '1 second', last_heartbeat_at = now()
            WHERE lease_id = @Id AND status = 'Active' AND client_id = @ClientId
            """,
            new { Id = leaseId, ClientId = clientId }, cancellationToken: ct));

        if (updated == 0)
        {
            var lease = await GetAsync(leaseId, ct);
            if (lease is null) throw new FabricException(ErrorCodes.LeaseNotFound, $"lease '{leaseId}' not found");
            if (lease.ClientId != clientId)
                throw new FabricException(ErrorCodes.LeaseOwnerMismatch, "heartbeat rejected: client is not the lease owner");
            throw new FabricException(ErrorCodes.LeaseExpired, $"lease '{leaseId}' is {lease.Status}; heartbeat rejected (AC-05-03)");
        }

        var expiresAt = await conn.QuerySingleAsync<DateTimeOffset>(new CommandDefinition(
            "SELECT expires_at FROM leases WHERE lease_id = @Id", new { Id = leaseId }, cancellationToken: ct));
        var serverTime = await conn.QuerySingleAsync<DateTimeOffset>(new CommandDefinition(
            "SELECT now()", cancellationToken: ct));

        return new HeartbeatResponse(leaseId, States.LeaseStatus.Active, expiresAt, serverTime);
    }

    public async Task<ReleaseResponse> ReleaseAsync(string leaseId, string clientId, string reason, CancellationToken ct)
    {
        var lease = await GetAsync(leaseId, ct)
            ?? throw new FabricException(ErrorCodes.LeaseNotFound, $"lease '{leaseId}' not found");
        if (lease.ClientId != clientId)
            throw new FabricException(ErrorCodes.LeaseOwnerMismatch, "release rejected: client is not the lease owner (AC-06-03)");

        await using var conn = await db.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var updated = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE leases SET status = 'Closed', released_at = now(), end_reason = @Reason
            WHERE lease_id = @Id AND status = 'Active'
            """,
            new { Id = leaseId, Reason = reason }, tx, cancellationToken: ct));

        if (updated == 0)
        {
            await tx.RollbackAsync(ct);
            var fresh = await GetAsync(leaseId, ct);
            if (fresh?.Status == States.LeaseStatus.Closed)
                return new ReleaseResponse(leaseId, States.LeaseStatus.Closed, "AlreadyReleased", time.GetUtcNow()); // AC-06-02 幂等
            throw new FabricException(ErrorCodes.LeaseExpired,
                $"lease '{leaseId}' already {fresh?.Status}; recovery handled by expiry path (11.2 Release/Expire 并发)");
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE resources SET operational_state = 'Recovering', updated_at = now() WHERE resource_id = @Rid AND operational_state = 'Busy'",
            new { Rid = lease.ResourceId }, tx, cancellationToken: ct));

        var evt = await audit.AppendAsync(new EventDraft(EventTypes.LeaseReleased, lease.ResourceId, leaseId, clientId,
            Payload: new() { ["reason"] = reason, ["durationSeconds"] = (lease.ExpiresAt - lease.AcquiredAt).TotalSeconds }), conn, tx, ct);
        await tx.CommitAsync(ct);
        audit.Publish(evt);

        // 清理在事务外执行（Adapter Reset 可能耗时），经资源串行队列排在在途指令之后
        var outcome = await gateway.ResetAsync(lease.ResourceId, ct);
        await FinalizeRecoveryAsync(lease.ResourceId, outcome, $"release:{reason}", ct);
        return new ReleaseResponse(leaseId, States.LeaseStatus.Closed, "Released", time.GetUtcNow());
    }

    public async Task<ForceReleaseResponse> ForceReleaseAsync(string leaseId, string reason, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new FabricException(ErrorCodes.ReasonRequired, "force release requires a reason (AC-12-04)");

        var lease = await GetAsync(leaseId, ct)
            ?? throw new FabricException(ErrorCodes.LeaseNotFound, $"lease '{leaseId}' not found");
        if (lease.Status != States.LeaseStatus.Active)
            throw new FabricException(ErrorCodes.StateConflict, $"lease '{leaseId}' is already {lease.Status}");

        // 先取消在途指令，再走安全清理（评审 P0-2）
        gateway.CancelCommands(leaseId);

        await using var conn = await db.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE leases SET status = 'Closed', released_at = now(), end_reason = @Reason
            WHERE lease_id = @Id AND status = 'Active'
            """,
            new { Id = leaseId, Reason = EndReasons.AdminForceRelease }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE resources SET operational_state = 'Recovering', updated_at = now() WHERE resource_id = @Rid AND operational_state = 'Busy'",
            new { Rid = lease.ResourceId }, tx, cancellationToken: ct));

        var evt = await audit.AppendAsync(new EventDraft(EventTypes.AdminOverride, lease.ResourceId, leaseId, lease.ClientId, actor,
            Payload: new() { ["action"] = "ForceRelease", ["reason"] = reason }), conn, tx, ct); // AC-11-03
        await tx.CommitAsync(ct);
        audit.Publish(evt);

        var outcome = await gateway.ResetAsync(lease.ResourceId, ct);
        await FinalizeRecoveryAsync(lease.ResourceId, outcome, "admin-force-release", ct);
        return new ForceReleaseResponse(leaseId, States.LeaseStatus.Closed, time.GetUtcNow());
    }

    /// <summary>过期扫描（由 LeaseExpiryScanner 周期调用）。Expire 与 Release/ForceRelease 通过 status='Active' 条件互斥。</summary>
    public async Task<int> RecoverExpiredLeasesAsync(CancellationToken ct)
    {
        await using var conn = await db.CreateOpenConnectionAsync(ct);
        var expired = (await conn.QueryAsync<Lease>(new CommandDefinition(
            """
            UPDATE leases SET status = 'Expired', end_reason = @Reason, released_at = now()
            WHERE status = 'Active' AND expires_at < now()
            RETURNING lease_id, resource_id, capability_id, client_id, idempotency_key, request_hash,
                      work_order, serial_number, operation, status, ttl_seconds, acquired_at, expires_at,
                      last_heartbeat_at, released_at, end_reason
            """,
            new { Reason = EndReasons.TimeoutExpired }, cancellationToken: ct))).ToList();

        foreach (var lease in expired)
        {
            await audit.EmitAsync(new EventDraft(EventTypes.HeartbeatLost, lease.ResourceId, lease.LeaseId, lease.ClientId,
                Payload: new() { ["lastHeartbeatAt"] = lease.LastHeartbeatAt }), ct);
            await audit.EmitAsync(new EventDraft(EventTypes.LeaseExpired, lease.ResourceId, lease.LeaseId, lease.ClientId,
                Payload: new() { ["ttlSeconds"] = lease.TtlSeconds }), ct);

            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE resources SET operational_state = 'Recovering', updated_at = now() WHERE resource_id = @Rid AND operational_state = 'Busy'",
                new { Rid = lease.ResourceId }, cancellationToken: ct));

            var outcome = await gateway.ResetAsync(lease.ResourceId, ct);
            await FinalizeRecoveryAsync(lease.ResourceId, outcome, "lease-expired", ct);
        }
        return expired.Count;
    }

    /// <summary>执行类端点的前置校验（FR-08 / AC-08-04/05）：租约必须 Active 且属于调用者。</summary>
    public async Task<Lease> ValidateActiveLeaseAsync(string leaseId, string clientId, CancellationToken ct)
    {
        var lease = await GetAsync(leaseId, ct)
            ?? throw new FabricException(ErrorCodes.LeaseNotFound, $"lease '{leaseId}' not found");
        if (lease.Status != States.LeaseStatus.Active)
            throw new FabricException(ErrorCodes.LeaseExpired, $"lease '{leaseId}' is {lease.Status}; execution rejected (AC-08-05)");
        if (lease.ClientId != clientId)
            throw new FabricException(ErrorCodes.LeaseOwnerMismatch, "execution rejected: client is not the lease owner");
        return lease;
    }

    public async Task<Lease?> GetAsync(string leaseId, CancellationToken ct)
    {
        await using var conn = await db.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Lease>(new CommandDefinition(
            "SELECT * FROM leases WHERE lease_id = @Id", new { Id = leaseId }, cancellationToken: ct));
    }

    public async Task<Lease?> FindByIdempotencyKeyAsync(string key, CancellationToken ct)
    {
        await using var conn = await db.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Lease>(new CommandDefinition(
            "SELECT * FROM leases WHERE idempotency_key = @Key", new { Key = key }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Lease>> ListAsync(string? status, string? resourceId, string? clientId, int limit, int offset, CancellationToken ct)
    {
        await using var conn = await db.CreateOpenConnectionAsync(ct);
        var where = "WHERE 1=1";
        var p = new DynamicParameters();
        if (!string.IsNullOrEmpty(status)) { where += " AND status = @Status"; p.Add("Status", status); }
        if (!string.IsNullOrEmpty(resourceId)) { where += " AND resource_id = @Rid"; p.Add("Rid", resourceId); }
        if (!string.IsNullOrEmpty(clientId)) { where += " AND client_id = @Cid"; p.Add("Cid", clientId); }
        p.Add("Limit", limit); p.Add("Offset", offset);
        var rows = await conn.QueryAsync<Lease>(new CommandDefinition(
            $"SELECT * FROM leases {where} ORDER BY acquired_at DESC, lease_id LIMIT @Limit OFFSET @Offset", p, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>Recovery 完成收尾：Recovering → Available（成功）/ Error（失败），并刷新 last_released_at。</summary>
    private async Task FinalizeRecoveryAsync(string resourceId, GatewayOutcome outcome, string cause, CancellationToken ct)
    {
        var target = outcome == GatewayOutcome.Succeeded ? States.Operational.Available : States.Operational.Error;
        await using var conn = await db.CreateOpenConnectionAsync(ct);
        var updated = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE resources
            SET operational_state = @Target,
                last_released_at = CASE WHEN @Target = 'Available' THEN now() ELSE last_released_at END,
                updated_at = now()
            WHERE resource_id = @Rid AND operational_state = 'Recovering'
            """,
            new { Target = target, Rid = resourceId }, cancellationToken: ct));

        if (updated > 0)
        {
            await audit.EmitAsync(new EventDraft(EventTypes.ResourceStateChanged, resourceId,
                Payload: new() { ["from"] = States.Operational.Recovering, ["to"] = target, ["cause"] = cause }), ct);
        }
    }

    private static void EnsureSameFingerprint(Lease existing, string fingerprint)
    {
        if (existing.RequestHash != fingerprint)
            throw new FabricException(ErrorCodes.IdempotencyKeyConflict,
                "idempotency key was already used with a different request body");
    }

    private AcquireResponse ToResponse(Lease lease, bool replay) => new(
        lease.LeaseId, lease.ResourceId, lease.CapabilityId, lease.ClientId, lease.Status,
        lease.AcquiredAt, lease.ExpiresAt, lease.TtlSeconds,
        new LeaseContext(
            string.IsNullOrEmpty(lease.WorkOrder) ? null : lease.WorkOrder,
            string.IsNullOrEmpty(lease.SerialNumber) ? null : lease.SerialNumber,
            string.IsNullOrEmpty(lease.Operation) ? null : lease.Operation),
        replay, time.GetUtcNow());

    public static LeaseDto ToDto(Lease lease) => new(
        lease.LeaseId, lease.ResourceId, lease.CapabilityId, lease.ClientId, lease.Status,
        lease.TtlSeconds, lease.AcquiredAt, lease.ExpiresAt, lease.LastHeartbeatAt,
        lease.ReleasedAt, lease.EndReason,
        new LeaseContext(
            string.IsNullOrEmpty(lease.WorkOrder) ? null : lease.WorkOrder,
            string.IsNullOrEmpty(lease.SerialNumber) ? null : lease.SerialNumber,
            string.IsNullOrEmpty(lease.Operation) ? null : lease.Operation));

    private static string NewLeaseId()
        => $"L-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

    private static string ComputeRequestHash(AcquireRequest req, Dictionary<string, ConstraintSpec> constraints)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            req.ClientId,
            req.Capability,
            Constraints = constraints.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => new { kv.Value.Operator, kv.Value.Value }),
            req.Context,
            Ttl = req.LeaseTtlSeconds,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
