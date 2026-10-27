using TestFabric.Api.Infrastructure;
using TestFabric.Contracts;
using TestFabric.Core.Services;

namespace TestFabric.Api.Endpoints;

public static class DiscoveryEndpoints
{
    /// <summary>POST /api/v1/resources/discover（PRD §8.1）。只读，不锁定（AC-03-05）。</summary>
    public static void MapDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/resources/discover", async (DiscoverRequest req, HttpContext ctx, DiscoveryService discovery) =>
        {
            ctx.RequireRole(Role.Viewer);
            return Results.Ok(await discovery.DiscoverAsync(req, ctx.RequestAborted));
        });
    }
}
