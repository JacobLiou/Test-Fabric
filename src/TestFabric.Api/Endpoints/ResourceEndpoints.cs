using TestFabric.Api.Infrastructure;
using TestFabric.Contracts;
using TestFabric.Core.Domain;
using TestFabric.Core.Services;

namespace TestFabric.Api.Endpoints;

public static class ResourceEndpoints
{
    public static void MapResourceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/resources");

        group.MapPost("/", async (CreateResourceRequest req, HttpContext ctx, RegistryService registry) =>
        {
            ctx.RequireRole(Role.EquipmentEngineer);
            var dto = await registry.CreateAsync(req, ctx.User.Identity!.Name!, ctx.RequestAborted);
            return Results.Created($"/api/v1/resources/{dto.ResourceId}", dto);
        });

        group.MapGet("/", async (HttpContext ctx, RegistryService registry,
            string? status, string? type, string? location, int? offset, int? limit) =>
        {
            ctx.RequireRole(Role.Viewer);
            var page = await registry.ListAsync(status, type, location, offset ?? 0, limit ?? 100, ctx.RequestAborted);
            return Results.Ok(page);
        });

        group.MapGet("/{resourceId}", async (string resourceId, HttpContext ctx, RegistryService registry) =>
        {
            ctx.RequireRole(Role.Viewer);
            var dto = await registry.GetDtoAsync(resourceId, ctx.RequestAborted);
            return dto is null ? throw NotFound(resourceId) : Results.Ok(dto);
        });

        group.MapPatch("/{resourceId}", async (string resourceId, UpdateResourceRequest req, HttpContext ctx, RegistryService registry) =>
        {
            ctx.RequireRole(Role.EquipmentEngineer);
            return Results.Ok(await registry.UpdateAsync(resourceId, req, ctx.User.Identity!.Name!, ctx.RequestAborted));
        });

        group.MapPost("/{resourceId}/enable", (string resourceId, HttpContext ctx, RegistryService registry) =>
            ChangeAdminState(resourceId, States.Administrative.Enabled, ctx, registry));
        group.MapPost("/{resourceId}/disable", (string resourceId, HttpContext ctx, RegistryService registry) =>
            ChangeAdminState(resourceId, States.Administrative.Disabled, ctx, registry));
        group.MapPost("/{resourceId}/maintenance", (string resourceId, HttpContext ctx, RegistryService registry) =>
            ChangeAdminState(resourceId, States.Administrative.Maintenance, ctx, registry));

        group.MapDelete("/{resourceId}", async (string resourceId, HttpContext ctx, RegistryService registry) =>
        {
            ctx.RequireRole(Role.EquipmentEngineer);
            await registry.DeleteAsync(resourceId, ctx.User.Identity!.Name!, ctx.RequestAborted); // AC-01-05 逻辑删除
            return Results.NoContent();
        });

        // FR-12-02：对 Error 资源执行复位确认
        group.MapPost("/{resourceId}/reset", async (string resourceId, HttpContext ctx, RegistryService registry) =>
        {
            ctx.RequireRole(Role.Administrator);
            return Results.Ok(await registry.ResetFromErrorAsync(resourceId, ctx.User.Identity!.Name!, ctx.RequestAborted));
        });
    }

    private static async Task<IResult> ChangeAdminState(string resourceId, string target, HttpContext ctx, RegistryService registry)
    {
        // Disable/Maintenance 需管理员；Enable 授权工程师即可
        ctx.RequireRole(target == States.Administrative.Enabled ? Role.EquipmentEngineer : Role.Administrator);
        return Results.Ok(await registry.SetAdministrativeStateAsync(resourceId, target, ctx.User.Identity!.Name!, ctx.RequestAborted));
    }

    private static FabricException NotFound(string id)
        => new(ErrorCodes.ResourceNotFound, $"resource '{id}' not found");
}
