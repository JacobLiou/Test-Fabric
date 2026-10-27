using System.Data;
using System.Text.Json;
using Dapper;
using Npgsql;
using TestFabric.Contracts;
using TestFabric.Core.Domain;
using TestFabric.Core.Events;
using TestFabric.Core.Persistence;

namespace TestFabric.Core.Services;

/// <summary>
/// 审计服务：事件 append-only 落库（FR-10），可选在事务内写入，
/// 提交后经 EventBus 广播给 SSE 订阅者。普通用户不可修改或删除审计记录。
/// </summary>
public sealed class AuditService(IDbConnectionFactory db, EventBus bus)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<FabricEventDto> AppendAsync(
        EventDraft draft,
        NpgsqlConnection? conn = null,
        IDbTransaction? tx = null,
        CancellationToken ct = default)
    {
        var ownsConnection = conn is null;
        conn ??= await db.CreateOpenConnectionAsync(ct);
        try
        {
            var eventId = "E-" + Guid.NewGuid().ToString("N")[..16];
            var payloadJson = JsonSerializer.Serialize(draft.Payload ?? [], JsonOptions);

            var row = await conn.QuerySingleAsync<(long Seq, DateTimeOffset OccurredAt)>(new CommandDefinition(
                """
                INSERT INTO events (event_id, event_type, resource_id, lease_id, client_id, actor, payload, occurred_at)
                VALUES (@EventId, @EventType, @ResourceId, @LeaseId, @ClientId, @Actor, @Payload::jsonb, now())
                RETURNING seq, occurred_at
                """,
                new
                {
                    EventId = eventId,
                    draft.EventType,
                    draft.ResourceId,
                    draft.LeaseId,
                    draft.ClientId,
                    draft.Actor,
                    Payload = payloadJson,
                },
                tx,
                cancellationToken: ct));

            return new FabricEventDto(row.Seq, eventId, draft.EventType, draft.ResourceId, draft.LeaseId,
                draft.ClientId, draft.Actor, row.OccurredAt, draft.Payload ?? []);
        }
        finally
        {
            if (ownsConnection) await conn.DisposeAsync();
        }
    }

    /// <summary>独立落库并广播（无外部事务时使用）。</summary>
    public async Task EmitAsync(EventDraft draft, CancellationToken ct = default)
    {
        var evt = await AppendAsync(draft, ct: ct);
        Publish(evt);
    }

    public void Publish(FabricEventDto evt) => bus.Publish(evt);
}
