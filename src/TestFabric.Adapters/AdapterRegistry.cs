using System.Collections.Concurrent;
using TestFabric.Core.Domain;
using TestFabric.Core.Ports;

namespace TestFabric.Adapters;

/// <summary>
/// Adapter 注册表：adapterId → adapter 实例。V0.1 内置 Simulator；
/// 真实仪器 Adapter 以新增实例注册进来（NFR-03：新增型号不改 Test Program）。
/// </summary>
public sealed class AdapterRegistry : IAdapterCatalog
{
    private readonly ConcurrentDictionary<string, IInstrumentAdapter> _adapters = new();

    public void Register(IInstrumentAdapter adapter)
        => _adapters[adapter.AdapterId] = adapter;

    public IInstrumentAdapter Get(string adapterId)
        => _adapters.TryGetValue(adapterId, out var adapter)
            ? adapter
            : throw new FabricExceptionAdapter(adapterId);

    public bool Exists(string adapterId) => _adapters.ContainsKey(adapterId);

    public string GetVersion(string adapterId)
        => _adapters.TryGetValue(adapterId, out var adapter) ? adapter.Version : "0.0.0";

    private sealed class FabricExceptionAdapter(string adapterId)
        : TestFabric.Core.Domain.FabricException(
            TestFabric.Contracts.ErrorCodes.AdapterUnavailable, $"adapter '{adapterId}' is not registered");
}
