using System.Security.Claims;
using System.Text.Json;

namespace TestFabric.Api.Infrastructure;

public enum Role
{
    Viewer = 0,
    TestClient = 1,
    EquipmentEngineer = 2,
    Administrator = 3,
}

/// <summary>
/// Token 认证中间件（FR-11）：V0.1 静态令牌，按 RBAC 映射角色。
/// 支持 Authorization: Bearer、X-Api-Token 头，以及 ?token= 查询参数（仅建议 EventSource/SSE 使用）。
/// 令牌永不写入日志或响应体。
/// </summary>
public sealed class TokenAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IReadOnlyDictionary<string, (Role Role, string Subject)> _tokens;

    private static readonly string[] PublicPrefixes = ["/swagger", "/healthz", "/favicon.ico", "/index.html"];

    public TokenAuthMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        var entries = config.GetSection("Auth:Tokens").Get<List<TokenEntry>>() ?? [];
        _tokens = entries.ToDictionary(t => t.Token, t => (Enum.Parse<Role>(t.Role, ignoreCase: true), t.Subject));
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path;
        if (path == "/" || PublicPrefixes.Any(p => path.StartsWithSegments(p) || path.Value!.StartsWith(p)))
        {
            await _next(ctx);
            return;
        }

        var token = ExtractToken(ctx);
        if (token is null || !_tokens.TryGetValue(token, out var entry))
        {
            await Problem.WriteAsync(ctx, 401, Contracts.ErrorCodes.Unauthorized, "missing or invalid API token");
            return;
        }

        var identity = new ClaimsIdentity(authenticationType: "Token");
        identity.AddClaim(new Claim("role", entry.Role.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, entry.Subject));
        ctx.User = new ClaimsPrincipal(identity);

        await _next(ctx);
    }

    private static string? ExtractToken(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();
        if (ctx.Request.Headers.TryGetValue("X-Api-Token", out var xToken))
            return xToken.ToString();
        if (ctx.Request.Query.TryGetValue("token", out var queryToken)) // EventSource 无法自定义头
            return queryToken.ToString();
        return null;
    }

    private sealed record TokenEntry(string Token, string Role, string Subject);
}
