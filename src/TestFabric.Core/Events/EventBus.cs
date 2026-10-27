using System.Collections.Concurrent;
using System.Threading.Channels;
using TestFabric.Contracts;

namespace TestFabric.Core.Events;

/// <summary>
/// 进程内事件总线（SSE 数据源）。审计落库成功后由 AuditService 广播。
/// V0.1 不引入消息队列：事件以 append-only 表为权威存储，总线仅服务于实时推送。
/// </summary>
public sealed class EventBus
{
    private readonly ConcurrentDictionary<Guid, Channel<FabricEventDto>> _subscribers = new();

    public IDisposable Subscribe(out ChannelReader<FabricEventDto> reader)
    {
        var channel = Channel.CreateBounded<FabricEventDto>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        reader = channel.Reader;
        return new Subscription(() => _subscribers.TryRemove(id, out _));
    }

    public void Publish(FabricEventDto evt)
    {
        foreach (var channel in _subscribers.Values)
            channel.Writer.TryWrite(evt);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
