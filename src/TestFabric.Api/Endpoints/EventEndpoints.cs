using System.Text.Json;
using Dapper;
using TestFabric.Api.Infrastructure;
using TestFabric.Contracts;
using TestFabric.Core.Events;
using TestFabric.Core.Persistence;

namespace TestFabric.Api.Endpoints;

public static class EventEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/events");

        // 审计查询（FR-10：按时间、资源、Lease、Client 查询）
        group.MapGet("/", async (HttpContext ctx, IDbConnectionFactory db,
            string? resourceId, string? leaseId, string? clientId, long? afterSeq, int? limit) =>
        {
            ctx.RequireRole(Role.Viewer);
            await using var conn = await db.CreateOpenConnectionAsync(ctx.RequestAborted);

            var where = "WHERE 1=1";
            var p = new DynamicParameters();
            if (!string.IsNullOrEmpty(resourceId)) { where += " AND resource_id = @Rid"; p.Add("Rid", resourceId); }
            if (!string.IsNullOrEmpty(leaseId)) { where += " AND lease_id = @Lid"; p.Add("Lid", leaseId); }
            if (!string.IsNullOrEmpty(clientId)) { where += " AND client_id = @Cid"; p.Add("Cid", clientId); }
            if (afterSeq is > 0) { where += " AND seq > @After"; p.Add("After", afterSeq.Value); }
            p.Add("Limit", Math.Clamp(limit ?? 50, 1, 500));

            var rows = (await conn.QueryAsync<(long Seq, string EventId, string EventType, string? ResourceId, string? LeaseId,
                string? ClientId, string? Actor, string Payload, DateTimeOffset OccurredAt)>(new CommandDefinition(
                $"""
                SELECT seq, event_id, event_type, resource_id, lease_id, client_id, actor, payload::text AS payload, occurred_at
                FROM events {where} ORDER BY seq DESC LIMIT @Limit
                """,
                p, cancellationToken: ctx.RequestAborted))).ToList();

            return Results.Ok(rows.Select(r => new FabricEventDto(r.Seq, r.EventId, r.EventType, r.ResourceId,
                r.LeaseId, r.ClientId, r.Actor, r.OccurredAt,
                JsonPlain.ToDictionary(r.Payload))));
        });

        // SSE 实时事件流（FR-09：Dashboard 实时推送）。EventSource 无法带自定义头 → ?token=
        group.MapGet("/stream", async (HttpContext ctx, EventBus bus) =>
        {
            ctx.RequireRole(Role.Viewer);
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";

            var filterResource = ctx.Request.Query["resourceId"].ToString();
            var sub = bus.Subscribe(out var reader);
            var writeLock = new SemaphoreSlim(1, 1);
            var aborted = ctx.RequestAborted;

            var pump = Task.Run(async () =>
            {
                await foreach (var evt in reader.ReadAllAsync(aborted))
                {
                    if (!string.IsNullOrEmpty(filterResource) && evt.ResourceId != filterResource) continue;
                    await writeLock.WaitAsync(aborted);
                    try
                    {
                        await ctx.Response.WriteAsync(
                            $"id: {evt.Seq}\nevent: {evt.EventType}\ndata: {JsonSerializer.Serialize(evt, JsonOptions)}\n\n", aborted);
                        await ctx.Response.Body.FlushAsync(aborted);
                    }
                    finally { writeLock.Release(); }
                }
            }, aborted);

            var keepAlive = Task.Run(async () =>
            {
                while (!aborted.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(15), aborted); }
                    catch (OperationCanceledException) { break; }
                    await writeLock.WaitAsync(aborted);
                    try
                    {
                        await ctx.Response.WriteAsync(": keep-alive\n\n", aborted);
                        await ctx.Response.Body.FlushAsync(aborted);
                    }
                    finally { writeLock.Release(); }
                }
            }, aborted);

            try
            {
                await Task.WhenAll(pump, keepAlive);
            }
            catch (OperationCanceledException) { /* 客户端断开 */ }
            catch (IOException) { /* 客户端断开 */ }
            finally
            {
                sub.Dispose();
                pump.Dispose();
                keepAlive.Dispose();
            }
        });
    }
}
