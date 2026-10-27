using TestFabric.Adapters.Simulator;
using TestFabric.Api.Infrastructure;
using TestFabric.Contracts;
using TestFabric.Core.Services;

namespace TestFabric.Api.Endpoints;

/// <summary>Simulator 故障注入（Administrator）：Soak 测试 / E2E-06 故障注入的开关面板。</summary>
public static class SimulatorEndpoints
{
    public static void MapSimulatorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/simulator");

        group.MapGet("/{resourceId}/faults", (string resourceId, HttpContext ctx, SimulatorFaults faults) =>
        {
            ctx.RequireRole(Role.Administrator);
            return Results.Ok(faults.Get(resourceId));
        });

        group.MapPost("/{resourceId}/faults", async (string resourceId, SimulatorFaultSettings settings,
            HttpContext ctx, SimulatorFaults faults, AuditService audit) =>
        {
            ctx.RequireRole(Role.Administrator);
            faults.Set(resourceId, settings);
            await audit.EmitAsync(new(EventTypes.SimulatorFaultSet, resourceId,
                Actor: ctx.User.Identity!.Name,
                Payload: new() { ["settings"] = settings.ToString() }), ctx.RequestAborted);
            return Results.Ok(faults.Get(resourceId));
        });

        group.MapDelete("/{resourceId}/faults", async (string resourceId, HttpContext ctx, SimulatorFaults faults, AuditService audit) =>
        {
            ctx.RequireRole(Role.Administrator);
            faults.Clear(resourceId);
            await audit.EmitAsync(new(EventTypes.SimulatorFaultSet, resourceId,
                Actor: ctx.User.Identity!.Name,
                Payload: new() { ["cleared"] = true }), ctx.RequestAborted);
            return Results.NoContent();
        });
    }
}
