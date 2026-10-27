using System.Collections.Concurrent;
using TestFabric.Core.Domain;

namespace TestFabric.Adapters.Simulator;

/// <summary>光功率计模拟器（FR-08-5：Configure / MeasurePower / Zero / Abort）。</summary>
public sealed class SimulatorPowerMeterAdapter(SimulatorFaults faults) : IPowerMeterAdapter
{
    public string AdapterId => "sim.pm";
    public string Version => "1.0.0";

    private readonly ConcurrentDictionary<string, int> _averaging = new();
    private readonly ConcurrentDictionary<string, HashSet<int>> _zeroed = new();

    public Task ConnectAsync(Resource resource, CancellationToken ct) => Task.CompletedTask;
    public Task DisconnectAsync(Resource resource, CancellationToken ct) => Task.CompletedTask;

    public async Task<bool> HealthCheckAsync(Resource resource, CancellationToken ct)
    {
        var f = faults.Get(resource.ResourceId);
        if (f.SlowResponseMs > 0) await Task.Delay(f.SlowResponseMs, ct);
        return !f.Offline && !f.FailHealthCheck;
    }

    public async Task ResetAsync(Resource resource, CancellationToken ct)
    {
        if (faults.Get(resource.ResourceId).FailReset)
            throw new AdapterException("SIM-RESET-FAIL", $"simulated reset failure on {resource.ResourceId}");
        _zeroed.TryRemove(resource.ResourceId, out _);
        await Task.CompletedTask;
    }

    public Task ConfigureAsync(Resource resource, Contracts.PmConfigureRequest request, CancellationToken ct)
    {
        EnsureOnline(resource);
        if (request.Channel < 1 || request.Channel > 4)
            throw new AdapterException("SIM-PM-CFG-001", "channel must be 1..4");
        _averaging[resource.ResourceId] = request.Channel;
        return Task.CompletedTask;
    }

    public Task<Contracts.PmPowerResult> MeasurePowerAsync(Resource resource, Contracts.PmMeasureRequest request, CancellationToken ct)
    {
        EnsureOnline(resource);
        if (faults.Get(resource.ResourceId).FailSweep)
            throw new AdapterException("SIM-PM-MEAS-ERR-17", $"simulated measurement failure on {resource.ResourceId}");

        // 确定性模拟：通道相关的稳定功率 + 微小噪声
        var basePower = -20.0 + request.Channel * 2.5;
        var noise = (request.WavelengthNm ?? 1550) % 7 * 0.01;
        var zeroed = _zeroed.TryGetValue(resource.ResourceId, out var ch) && ch.Contains(request.Channel);
        var power = zeroed ? Math.Round(noise, 4) : Math.Round(basePower + noise, 4);

        return Task.FromResult(new Contracts.PmPowerResult(
            resource.ResourceId, Version, DateTimeOffset.UtcNow, request.Channel, power));
    }

    public Task<Contracts.PmZeroResult> ZeroAsync(Resource resource, int channel, CancellationToken ct)
    {
        EnsureOnline(resource);
        _zeroed.GetOrAdd(resource.ResourceId, _ => []).Add(channel);
        return Task.FromResult(new Contracts.PmZeroResult(resource.ResourceId, Version, DateTimeOffset.UtcNow, channel));
    }

    public Task AbortAsync(Resource resource, CancellationToken ct)
    {
        EnsureOnline(resource);
        return Task.CompletedTask;
    }

    private void EnsureOnline(Resource resource)
    {
        if (faults.Get(resource.ResourceId).Offline)
            throw new AdapterException("SIM-OFFLINE", $"simulated device offline: {resource.ResourceId}");
    }
}
