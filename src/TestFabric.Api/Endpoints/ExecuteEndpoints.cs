using TestFabric.Adapters;
using TestFabric.Api.Infrastructure;
using TestFabric.Contracts;
using TestFabric.Core.Domain;
using TestFabric.Core.Ports;
using TestFabric.Core.Services;

namespace TestFabric.Api.Endpoints;

/// <summary>
/// 执行能力 API（PRD §8.5）。所有命令先验证：租约 Active、资源与租约一致、调用者是 Owner（AC-08-04/05），
/// 再经资源串行队列派发到 Adapter。
/// </summary>
public static class ExecuteEndpoints
{
    public static void MapExecuteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/leases/{leaseId}");

        // ---------- OSA ----------
        group.MapPost("/osa/configure", async (string leaseId, OsaConfigureCommand cmd, HttpContext ctx, Exec env) =>
        {
            ctx.RequireRole(Role.TestClient);
            var (lease, resource, adapter) = await env.ResolveAsync<IOsaAdapter>(leaseId, cmd.ClientId, ctx.RequestAborted);
            return Results.Ok(await env.Gateway.ExecuteAsync(resource.ResourceId, lease.LeaseId,
                async token =>
                {
                    await adapter.ConfigureAsync(resource, new OsaConfigureRequest(cmd.StartNm, cmd.StopNm, cmd.ResolutionNm, cmd.TraceId), token);
                    return new OsaConfigureResult(resource.ResourceId, adapter.Version, DateTimeOffset.UtcNow);
                }, ctx.RequestAborted));
        });

        group.MapPost("/osa/single-sweep", async (string leaseId, OsaSweepCommand cmd, HttpContext ctx, Exec env) =>
        {
            ctx.RequireRole(Role.TestClient);
            var (lease, resource, adapter) = await env.ResolveAsync<IOsaAdapter>(leaseId, cmd.ClientId, ctx.RequestAborted);
            return Results.Ok(await env.Gateway.ExecuteAsync(resource.ResourceId, lease.LeaseId,
                async token => await adapter.SingleSweepAsync(resource, new OsaSweepRequest(cmd.TraceId), token),
                ctx.RequestAborted));
        });

        group.MapPost("/osa/get-trace", async (string leaseId, OsaTraceCommand cmd, HttpContext ctx, Exec env) =>
        {
            ctx.RequireRole(Role.TestClient);
            var (lease, resource, adapter) = await env.ResolveAsync<IOsaAdapter>(leaseId, cmd.ClientId, ctx.RequestAborted);
            return Results.Ok(await env.Gateway.ExecuteAsync(resource.ResourceId, lease.LeaseId,
                async token => await adapter.GetTraceAsync(resource, cmd.SweepId, token),
                ctx.RequestAborted));
        });

        group.MapPost("/osa/get-peak", async (string leaseId, OsaPeakCommand cmd, HttpContext ctx, Exec env) =>
        {
            ctx.RequireRole(Role.TestClient);
            var (lease, resource, adapter) = await env.ResolveAsync<IOsaAdapter>(leaseId, cmd.ClientId, ctx.RequestAborted);
            return Results.Ok(await env.Gateway.ExecuteAsync(resource.ResourceId, lease.LeaseId,
                async token => await adapter.GetPeakAsync(resource, cmd.SweepId, token),
                ctx.RequestAborted));
        });

        group.MapPost("/osa/abort", async (string leaseId, AbortCommand cmd, HttpContext ctx, Exec env) =>
        {
            ctx.RequireRole(Role.TestClient);
            var (lease, resource, adapter) = await env.ResolveAsync<IOsaAdapter>(leaseId, cmd.ClientId, ctx.RequestAborted);
            return Results.Ok(await env.Gateway.ExecuteAsync(resource.ResourceId, lease.LeaseId,
                async token =>
                {
                    await adapter.AbortAsync(resource, token);
                    return new CommandAck(resource.ResourceId, adapter.Version, DateTimeOffset.UtcNow, "OsaAbort");
                }, ctx.RequestAborted));
        });

        // ---------- Power Meter ----------
        group.MapPost("/power-meter/configure", async (string leaseId, PmConfigureCommand cmd, HttpContext ctx, Exec env) =>
        {
            ctx.RequireRole(Role.TestClient);
            var (lease, resource, adapter) = await env.ResolveAsync<IPowerMeterAdapter>(leaseId, cmd.ClientId, ctx.RequestAborted);
            return Results.Ok(await env.Gateway.ExecuteAsync(resource.ResourceId, lease.LeaseId,
                async token =>
                {
                    await adapter.ConfigureAsync(resource, new PmConfigureRequest(cmd.Channel, cmd.Unit), token);
                    return new PmConfigureResult(resource.ResourceId, adapter.Version, DateTimeOffset.UtcNow);
                }, ctx.RequestAborted));
        });

        group.MapPost("/power-meter/measure-power", async (string leaseId, PmMeasureCommand cmd, HttpContext ctx, Exec env) =>
        {
            ctx.RequireRole(Role.TestClient);
            var (lease, resource, adapter) = await env.ResolveAsync<IPowerMeterAdapter>(leaseId, cmd.ClientId, ctx.RequestAborted);
            return Results.Ok(await env.Gateway.ExecuteAsync(resource.ResourceId, lease.LeaseId,
                async token => await adapter.MeasurePowerAsync(resource, new PmMeasureRequest(cmd.Channel, cmd.WavelengthNm, cmd.AveragingCount), token),
                ctx.RequestAborted));
        });

        group.MapPost("/power-meter/zero", async (string leaseId, PmZeroCommand cmd, HttpContext ctx, Exec env) =>
        {
            ctx.RequireRole(Role.TestClient);
            var (lease, resource, adapter) = await env.ResolveAsync<IPowerMeterAdapter>(leaseId, cmd.ClientId, ctx.RequestAborted);
            return Results.Ok(await env.Gateway.ExecuteAsync(resource.ResourceId, lease.LeaseId,
                async token => await adapter.ZeroAsync(resource, cmd.Channel, token),
                ctx.RequestAborted));
        });

        group.MapPost("/power-meter/abort", async (string leaseId, AbortCommand cmd, HttpContext ctx, Exec env) =>
        {
            ctx.RequireRole(Role.TestClient);
            var (lease, resource, adapter) = await env.ResolveAsync<IPowerMeterAdapter>(leaseId, cmd.ClientId, ctx.RequestAborted);
            return Results.Ok(await env.Gateway.ExecuteAsync(resource.ResourceId, lease.LeaseId,
                async token =>
                {
                    await adapter.AbortAsync(resource, token);
                    return new CommandAck(resource.ResourceId, adapter.Version, DateTimeOffset.UtcNow, "PmAbort");
                }, ctx.RequestAborted));
        });
    }

    /// <summary>端点共用上下文（租约校验 + 资源加载 + Adapter 解析）。</summary>
    public sealed class Exec(LeaseService leases, RegistryService registry, AdapterRegistry adapters, IInstrumentGateway gateway)
    {
        public IInstrumentGateway Gateway => gateway;

        public async Task<(Lease Lease, Resource Resource, TAdapter Adapter)> ResolveAsync<TAdapter>(
            string leaseId, string clientId, CancellationToken ct) where TAdapter : class, IInstrumentAdapter
        {
            var lease = await leases.ValidateActiveLeaseAsync(leaseId, clientId, ct);
            var resource = await registry.GetAsync(lease.ResourceId, ct)
                ?? throw new FabricException(ErrorCodes.ResourceNotFound, $"resource '{lease.ResourceId}' vanished");
            var adapter = adapters.Get(resource.AdapterId) as TAdapter
                ?? throw new FabricException(ErrorCodes.AdapterNotCompatible,
                    $"adapter '{resource.AdapterId}' on {resource.ResourceId} does not implement the requested capability");
            return (lease, resource, adapter);
        }
    }
}
