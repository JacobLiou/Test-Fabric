namespace TestFabric.Core.Ports;

/// <summary>
/// Core 定义、Adapters 实现的端口。Core 不依赖任何具体仪器驱动。
/// 所有仪器指令（业务测量、健康探测、Recovery Reset）都经由该网关，
/// 以保证同一资源的指令严格串行（评审 P2：per-resource I/O 互斥），
/// 并支持按租约取消在途指令（评审 P0-2：Force Release 与长指令竞态）。
/// </summary>
public interface IInstrumentGateway
{
    /// <summary>在指定资源的串行队列上执行命令。leaseId 用于在途指令取消。</summary>
    Task<TResult> ExecuteAsync<TResult>(
        string resourceId,
        string? leaseId,
        Func<CancellationToken, Task<TResult>> command,
        CancellationToken cancellationToken);

    /// <summary>安全清理（Stop/Reset/Disconnect），用于 Release 与 Recovery。排在在途指令之后执行。</summary>
    Task<GatewayOutcome> ResetAsync(string resourceId, CancellationToken cancellationToken);

    /// <summary>
    /// L2 健康探测（Adapter 基础查询）。若队列忙于长指令则在 waitMs 后返回 Skipped（跳过 ≠ 失败）。
    /// Busy 资源的健康检查不得干扰测试（FR-07-05）。
    /// </summary>
    Task<HealthProbeResult> ProbeHealthAsync(string resourceId, int waitMs, CancellationToken cancellationToken);

    /// <summary>取消某租约的全部在途/排队指令（Force Release 前调用）。</summary>
    void CancelCommands(string leaseId);
}

public enum GatewayOutcome
{
    Succeeded,
    Failed,
}

public enum HealthProbeResult
{
    Healthy,
    Unhealthy,
    Skipped,
}

/// <summary>Adapter 目录：注册资源时校验 adapterId 合法性并查询版本。</summary>
public interface IAdapterCatalog
{
    bool Exists(string adapterId);
    string GetVersion(string adapterId);
}
