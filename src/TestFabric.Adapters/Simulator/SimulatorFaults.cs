using System.Collections.Concurrent;

namespace TestFabric.Adapters.Simulator;

/// <summary>
/// Simulator 故障注入开关（NFR-03 / 评审 P3：故障注入是可脚本化的测试基建）。
/// 每资源独立配置；由管理端点 /api/v1/simulator/{resourceId}/faults 设置（仅 Administrator）。
/// 用途：Soak 测试中模拟设备断网、Reset 失败、测量报错、慢响应等（E2E-06）。
/// </summary>
public sealed record SimulatorFaultSettings(
    bool Offline = false,
    bool FailReset = false,
    bool FailSweep = false,
    bool FailHealthCheck = false,
    int SlowResponseMs = 0);

public sealed class SimulatorFaults
{
    private readonly ConcurrentDictionary<string, SimulatorFaultSettings> _settings = new();

    public SimulatorFaultSettings Get(string resourceId)
        => _settings.TryGetValue(resourceId, out var s) ? s : new SimulatorFaultSettings();

    public void Set(string resourceId, SimulatorFaultSettings settings)
        => _settings[resourceId] = settings;

    public void Clear(string resourceId) => _settings.TryRemove(resourceId, out _);
}
