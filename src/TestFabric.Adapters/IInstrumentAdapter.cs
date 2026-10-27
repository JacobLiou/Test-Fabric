using TestFabric.Core.Domain;

namespace TestFabric.Adapters;

/// <summary>
/// 仪器 Adapter 契约（FR-08）。业务代码只依赖能力接口，品牌差异由 Adapter 转换。
/// 真实 VISA Adapter 的接入指南见 README（How to add an adapter）。
/// </summary>
public interface IInstrumentAdapter
{
    string AdapterId { get; }
    string Version { get; }

    Task ConnectAsync(Resource resource, CancellationToken ct);
    Task DisconnectAsync(Resource resource, CancellationToken ct);

    /// <summary>L2 健康查询（如 *IDN?）。必须可安全重复调用，且不干扰测量。</summary>
    Task<bool> HealthCheckAsync(Resource resource, CancellationToken ct);

    /// <summary>安全清理（Stop → Reset → Disconnect），用于 Release/Recovery。幂等。</summary>
    Task ResetAsync(Resource resource, CancellationToken ct);
}

/// <summary>OSA 统一能力接口（FR-08-4）。</summary>
public interface IOsaAdapter : IInstrumentAdapter
{
    Task ConfigureAsync(Resource resource, Contracts.OsaConfigureRequest request, CancellationToken ct);
    Task<Contracts.OsaSweepResult> SingleSweepAsync(Resource resource, Contracts.OsaSweepRequest request, CancellationToken ct);
    Task<Contracts.OsaTrace> GetTraceAsync(Resource resource, string sweepId, CancellationToken ct);
    Task<Contracts.OsaPeak> GetPeakAsync(Resource resource, string sweepId, CancellationToken ct);
    Task AbortAsync(Resource resource, CancellationToken ct);
}

/// <summary>光功率计统一能力接口（FR-08-5）。</summary>
public interface IPowerMeterAdapter : IInstrumentAdapter
{
    Task ConfigureAsync(Resource resource, Contracts.PmConfigureRequest request, CancellationToken ct);
    Task<Contracts.PmPowerResult> MeasurePowerAsync(Resource resource, Contracts.PmMeasureRequest request, CancellationToken ct);
    Task<Contracts.PmZeroResult> ZeroAsync(Resource resource, int channel, CancellationToken ct);
    Task AbortAsync(Resource resource, CancellationToken ct);
}

/// <summary>Adapter 异常：保留仪器原始错误码，由 API 层映射为统一错误码（AC-08-02）。</summary>
public class AdapterException : Exception
{
    public string InstrumentCode { get; }

    public AdapterException(string instrumentCode, string message) : base(message)
        => InstrumentCode = instrumentCode;
}
