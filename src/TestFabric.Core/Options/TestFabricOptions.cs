namespace TestFabric.Core.Options;

public sealed class TestFabricOptions
{
    public const string SectionName = "TestFabric";

    public AcquireOptions Acquire { get; set; } = new();
    public LeaseOptions Lease { get; set; } = new();
    public ScannerOptions Scanner { get; set; } = new();
    public HealthOptions Health { get; set; } = new();
    public StartupOptions Startup { get; set; } = new();
}

public sealed class AcquireOptions
{
    public int DefaultWaitTimeoutSeconds { get; set; } = 5;
    public int PollIntervalMs { get; set; } = 250;
}

public sealed class LeaseOptions
{
    public int DefaultTtlSeconds { get; set; } = 60;
    public int MinTtlSeconds { get; set; } = 5;
    public int MaxTtlSeconds { get; set; } = 3600;
}

public sealed class ScannerOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalMs { get; set; } = 1000;
}

public sealed class HealthOptions
{
    public int IntervalSeconds { get; set; } = 30;
    public int FailThreshold { get; set; } = 3;
    public int SuccessThreshold { get; set; } = 2;
    public int L1TimeoutMs { get; set; } = 2000;
    public int ProbeWaitMs { get; set; } = 2000;
}

public sealed class StartupOptions
{
    /// <summary>
    /// 服务重启后是否对所有 Active Lease 重新起算 TTL（重启宽限期）。
    /// 防止重启期间丢失的心跳被误判为客户端崩溃，触发集中 Reset 风暴（评审 P0-1）。
    /// </summary>
    public bool LeaseGraceEnabled { get; set; } = true;
}
