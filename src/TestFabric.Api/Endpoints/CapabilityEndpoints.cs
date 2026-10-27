using TestFabric.Api.Infrastructure;
using TestFabric.Contracts;
using TestFabric.Core.Services;

namespace TestFabric.Api.Endpoints;

public static class CapabilityEndpoints
{
    public static void MapCapabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/capabilities");

        group.MapPost("/", async (CreateCapabilityRequest req, HttpContext ctx, CapabilityService capabilities) =>
        {
            ctx.RequireRole(Role.EquipmentEngineer);
            var dto = await capabilities.CreateAsync(req, ctx.User.Identity!.Name!, ctx.RequestAborted);
            return Results.Created($"/api/v1/capabilities/{dto.CapabilityId}", dto);
        });

        group.MapGet("/", async (HttpContext ctx, CapabilityService capabilities) =>
        {
            ctx.RequireRole(Role.Viewer);
            return Results.Ok(await capabilities.ListAsync(ctx.RequestAborted));
        });
    }
}
