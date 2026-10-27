namespace TestFabric.Core.Domain;

/// <summary>待写入的领域事件草稿（AuditService 落库并广播）。</summary>
public record EventDraft(
    string EventType,
    string? ResourceId = null,
    string? LeaseId = null,
    string? ClientId = null,
    string? Actor = null,
    Dictionary<string, object?>? Payload = null);
