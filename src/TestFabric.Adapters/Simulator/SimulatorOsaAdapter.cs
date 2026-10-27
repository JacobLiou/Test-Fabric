using System.Collections.Concurrent;
using TestFabric.Adapters.Simulator;
using TestFabric.Core.Domain;

namespace TestFabric.Adapters.Simulator;

/// <summary>
/// OSA 模拟器（FR-08 最小接口）：Configure / SingleSweep / GetTrace / GetPeak / Abort。
/// 生成的轨迹 = 底噪 + 中心高斯峰，行为确定且可注入故障。
/// </summary>
public sealed class SimulatorOsaAdapter(SimulatorFaults faults) : IOsaAdapter
{
    public string AdapterId => "sim.osa";
    public string Version => "1.0.0";

    private sealed record OsaConfig(double StartNm, double StopNm, double ResolutionNm);

    private readonly ConcurrentDictionary<string, OsaConfig> _configs = new();
    private readonly ConcurrentDictionary<string, Contracts.OsaTrace> _traces = new();

    public Task ConnectAsync(Resource resource, CancellationToken ct) => Task.CompletedTask;
    public Task DisconnectAsync(Resource resource, CancellationToken ct) => Task.CompletedTask;

    public async Task<bool> HealthCheckAsync(Resource resource, CancellationToken ct)
    {
        await MaybeDelayAsync(resource, ct);
        var f = faults.Get(resource.ResourceId);
        return !f.Offline && !f.FailHealthCheck;
    }

    public async Task ResetAsync(Resource resource, CancellationToken ct)
    {
        await MaybeDelayAsync(resource, ct);
        if (faults.Get(resource.ResourceId).FailReset)
            throw new AdapterException("SIM-RESET-FAIL", $"simulated reset failure on {resource.ResourceId}");
        _configs.TryRemove(resource.ResourceId, out _);
    }

    public async Task ConfigureAsync(Resource resource, Contracts.OsaConfigureRequest request, CancellationToken ct)
    {
        await MaybeDelayAsync(resource, ct);
        EnsureOnline(resource);
        if (request.StartNm >= request.StopNm)
            throw new AdapterException("SIM-OSA-CFG-001", "startNm must be less than stopNm");
        if (request.ResolutionNm <= 0)
            throw new AdapterException("SIM-OSA-CFG-002", "resolutionNm must be positive");
        _configs[resource.ResourceId] = new OsaConfig(request.StartNm, request.StopNm, request.ResolutionNm);
    }

    public async Task<Contracts.OsaSweepResult> SingleSweepAsync(Resource resource, Contracts.OsaSweepRequest request, CancellationToken ct)
    {
        await MaybeDelayAsync(resource, ct);
        EnsureOnline(resource);
        if (faults.Get(resource.ResourceId).FailSweep)
            throw new AdapterException("SIM-OSA-SWEEP-ERR-42", $"simulated sweep failure on {resource.ResourceId}");

        var config = _configs.GetOrAdd(resource.ResourceId, _ => new OsaConfig(1260, 1360, 0.05));
        var sweepId = $"SW-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}";
        var trace = GenerateTrace(resource, sweepId, config);
        _traces[sweepId] = trace;

        return new Contracts.OsaSweepResult(resource.ResourceId, Version, sweepId, DateTimeOffset.UtcNow, trace.ValuesDbm.Length);
    }

    public Task<Contracts.OsaTrace> GetTraceAsync(Resource resource, string sweepId, CancellationToken ct)
    {
        EnsureOnline(resource);
        return _traces.TryGetValue(sweepId, out var trace)
            ? Task.FromResult(trace)
            : throw new AdapterException("SIM-OSA-TRACE-404", $"sweep '{sweepId}' not found");
    }

    public Task<Contracts.OsaPeak> GetPeakAsync(Resource resource, string sweepId, CancellationToken ct)
    {
        EnsureOnline(resource);
        if (!_traces.TryGetValue(sweepId, out var trace))
            throw new AdapterException("SIM-OSA-TRACE-404", $"sweep '{sweepId}' not found");

        var peakIdx = Array.IndexOf(trace.ValuesDbm, trace.ValuesDbm.Max());
        var step = (trace.StopNm - trace.StartNm) / Math.Max(1, trace.ValuesDbm.Length - 1);
        return Task.FromResult(new Contracts.OsaPeak(resource.ResourceId, Version, sweepId, trace.Timestamp,
            trace.StartNm + peakIdx * step, trace.ValuesDbm[peakIdx]));
    }

    public Task AbortAsync(Resource resource, CancellationToken ct)
    {
        EnsureOnline(resource);
        return Task.CompletedTask;
    }

    private Contracts.OsaTrace GenerateTrace(Resource resource, string sweepId, OsaConfig config)
    {
        var span = config.StopNm - config.StartNm;
        var points = (int)Math.Clamp(Math.Round(span / config.ResolutionNm), 128, 5001);
        var values = new double[points];
        var center = config.StartNm + span / 2;
        var sigma = Math.Max(span / 20, config.ResolutionNm * 10);
        var rng = new Random(sweepId.GetHashCode());

        for (var i = 0; i < points; i++)
        {
            var x = config.StartNm + span * i / Math.Max(1, points - 1);
            var gaussian = -3.5 * Math.Exp(-Math.Pow(x - center, 2) / (2 * sigma * sigma)); // -3.5 dBm 峰
            values[i] = Math.Round(gaussian + (rng.NextDouble() - 0.5) * 0.02, 4);            // ±0.01 dBm 噪声
        }
        return new Contracts.OsaTrace(resource.ResourceId, Version, sweepId, DateTimeOffset.UtcNow,
            config.StartNm, config.StopNm, "nm", values);
    }

    private void EnsureOnline(Resource resource)
    {
        if (faults.Get(resource.ResourceId).Offline)
            throw new AdapterException("SIM-OFFLINE", $"simulated device offline: {resource.ResourceId}");
    }

    private async Task MaybeDelayAsync(Resource resource, CancellationToken ct)
    {
        var slow = faults.Get(resource.ResourceId).SlowResponseMs;
        if (slow > 0) await Task.Delay(slow, ct);
    }
}
