namespace TestFabric.Contracts;

/// <summary>执行类命令请求：clientId 用于租约 Owner 校验（AC-08-04）。</summary>
public record OsaConfigureCommand(string ClientId, double StartNm, double StopNm, double ResolutionNm, string? TraceId = null);
public record OsaSweepCommand(string ClientId, string? TraceId = null);
public record OsaTraceCommand(string ClientId, string SweepId);
public record OsaPeakCommand(string ClientId, string SweepId);
public record AbortCommand(string ClientId);
public record PmConfigureCommand(string ClientId, int Channel, string Unit = "dBm");
public record PmMeasureCommand(string ClientId, int Channel, double? WavelengthNm = null, int AveragingCount = 1);
public record PmZeroCommand(string ClientId, int Channel);
