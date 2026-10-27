using TestFabric.Api.Infrastructure;
using TestFabric.Contracts;
using TestFabric.Core.Services;

namespace TestFabric.Api.Endpoints;

public static class LeaseEndpoints
{
    public static void MapLeaseEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/leases");

        // PRD §8.2：Idempotency-Key 头（AC-04-03）
        group.MapPost("/acquire", async (HttpContext ctx, LeaseService leases, AcquireRequest req) =>
        {
            ctx.RequireRole(Role.TestClient);
            var idempotencyKey = ctx.Request.Headers.TryGetValue("Idempotency-Key", out var key) && !string.IsNullOrEmpty(key)
                ? key.ToString()
                : null;
            var result = await leases.AcquireAsync(req, idempotencyKey, ctx.RequestAborted);
            return result.IdempotentReplay
                ? Results.Ok(result)
                : Results.Created($"/api/v1/leases/{result.LeaseId}", result);
        });

        // PRD §8.3
        group.MapPost("/{leaseId}/heartbeat", async (string leaseId, HeartbeatRequest req, HttpContext ctx, LeaseService leases) =>
        {
            ctx.RequireRole(Role.TestClient);
            return Results.Ok(await leases.HeartbeatAsync(leaseId, req.ClientId, ctx.RequestAborted));
        });

        // PRD §8.4
        group.MapPost("/{leaseId}/release", async (string leaseId, ReleaseRequest req, HttpContext ctx, LeaseService leases) =>
        {
            ctx.RequireRole(Role.TestClient);
            return Results.Ok(await leases.ReleaseAsync(leaseId, req.ClientId, req.Reason, ctx.RequestAborted));
        });

        // FR-12：管理员强制释放，必须填写原因（AC-12-04）
        group.MapPost("/{leaseId}/force-release", async (string leaseId, ForceReleaseRequest req, HttpContext ctx, LeaseService leases) =>
        {
            ctx.RequireRole(Role.Administrator);
            return Results.Ok(await leases.ForceReleaseAsync(leaseId, req.Reason, ctx.User.Identity!.Name!, ctx.RequestAborted));
        });

        group.MapGet("/", async (HttpContext ctx, LeaseService leases,
            string? status, string? resourceId, string? clientId, int? offset, int? limit) =>
        {
            ctx.RequireRole(Role.Viewer);
            var items = await leases.ListAsync(status, resourceId, clientId, limit ?? 50, offset ?? 0, ctx.RequestAborted);
            var dtos = items.Select(LeaseService.ToDto).ToList();
            return Results.Ok(new PagedResult<LeaseDto>(dtos, dtos.Count, offset ?? 0, limit ?? 50));
        });

        group.MapGet("/{leaseId}", async (string leaseId, HttpContext ctx, LeaseService leases) =>
        {
            ctx.RequireRole(Role.Viewer);
            var lease = await leases.GetAsync(leaseId, ctx.RequestAborted)
                ?? throw new FabricLeaseNotFound(leaseId);
            return Results.Ok(LeaseService.ToDto(lease));
        });
    }

    private sealed class FabricLeaseNotFound(string leaseId)
        : Core.Domain.FabricException(ErrorCodes.LeaseNotFound, $"lease '{leaseId}' not found");
}
