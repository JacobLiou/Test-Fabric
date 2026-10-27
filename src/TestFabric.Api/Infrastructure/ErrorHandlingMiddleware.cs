using System.Text.Json;
using TestFabric.Contracts;
using TestFabric.Core.Domain;

namespace TestFabric.Api.Infrastructure;

/// <summary>统一错误出口：FabricException → RFC9457 problem+json；所有响应携带 traceId（NFR-04）。</summary>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (FabricException fex)
        {
            var status = ErrorCodes.ToHttpStatus(fex.Code);
            logger.LogInformation("Fabric error {Code} on {Method} {Path}: {Message}",
                fex.Code, ctx.Request.Method, ctx.Request.Path, fex.Message);
            await Problem.WriteAsync(ctx, status, fex.Code, fex.Message, fex.Details);
        }
        catch (BadHttpRequestException bex)
        {
            await Problem.WriteAsync(ctx, 400, ErrorCodes.ValidationFailed, bex.Message);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // 客户端已断开，无法也无需响应
        }
        catch (TaskCanceledException)
        {
            // 命令被租约取消（ForceRelease）而非 HTTP 断开
            await Problem.WriteAsync(ctx, 409, ErrorCodes.CommandCancelled, "command cancelled (lease was released or force-released)");
        }
        catch (OperationCanceledException)
        {
            await Problem.WriteAsync(ctx, 409, ErrorCodes.CommandCancelled, "command cancelled");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception on {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
            await Problem.WriteAsync(ctx, 500, "INTERNAL_ERROR", "internal error; see server logs (traceId in response)");
        }
    }
}

/// <summary>角色门槛（FR-11 权限矩阵）。</summary>
public static class RoleGate
{
    public static void RequireRole(this HttpContext ctx, Role minimum)
    {
        var current = ctx.User.FindFirst("role")?.Value;
        if (!Enum.TryParse<Role>(current, out var role) || role < minimum)
            throw new FabricException(ErrorCodes.Forbidden, $"operation requires role '{minimum}' (current: '{current ?? "anonymous"}')");
    }
}

/// <summary>problem+json 输出。错误码 → HTTP 状态映射见 Contracts.ErrorCodes。</summary>
public static class Problem
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(HttpContext ctx, int status, string code, string detail,
        Dictionary<string, object?>? extra = null)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/problem+json";
        var body = new
        {
            type = $"/errors/{code.ToLowerInvariant()}",
            title = code,
            status,
            detail,
            code,
            traceId = System.Diagnostics.Activity.Current?.Id ?? ctx.TraceIdentifier,
            details = extra,
        };
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOptions));
    }
}
