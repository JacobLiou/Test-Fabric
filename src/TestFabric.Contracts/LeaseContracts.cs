namespace TestFabric.Contracts;

public record AcquireRequest(
    string ClientId,
    string Capability,
    Dictionary<string, System.Text.Json.JsonElement>? Constraints = null,
    LeaseContext? Context = null,
    int? LeaseTtlSeconds = null,
    int? WaitTimeoutSeconds = null);

public record LeaseContext(string? WorkOrder = null, string? SerialNumber = null, string? Operation = null);

public record AcquireResponse(
    string LeaseId,
    string ResourceId,
    string Capability,
    string ClientId,
    string Status,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt,
    int TtlSeconds,
    LeaseContext? Context,
    bool IdempotentReplay,
    DateTimeOffset ServerTime);

public record LeaseDto(
    string LeaseId,
    string ResourceId,
    string Capability,
    string ClientId,
    string Status,
    int TtlSeconds,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset? ReleasedAt,
    string? EndReason,
    LeaseContext? Context);

public record HeartbeatRequest(string ClientId);

public record HeartbeatResponse(
    string LeaseId,
    string Status,
    DateTimeOffset ExpiresAt,
    DateTimeOffset ServerTime);

public record ReleaseRequest(string ClientId, string Reason = "Completed");

public record ReleaseResponse(string LeaseId, string Status, string Result, DateTimeOffset ServerTime);

public record ForceReleaseRequest(string Reason);

public record ForceReleaseResponse(string LeaseId, string Status, DateTimeOffset ServerTime);

/// <summary>租约结束原因（FR-06）。</summary>
public static class EndReasons
{
    public const string Completed = "Completed";
    public const string TestFailed = "TestFailed";
    public const string Cancelled = "Cancelled";
    public const string TimeoutExpired = "TimeoutExpired";
    public const string AdminForceRelease = "AdminForceRelease";
    public const string StartupGrace = "StartupGrace";
}
