namespace TestFabric.Contracts;

public record FabricEventDto(
    long Seq,
    string EventId,
    string EventType,
    string? ResourceId,
    string? LeaseId,
    string? ClientId,
    string? Actor,
    DateTimeOffset OccurredAt,
    Dictionary<string, object?> Payload);

public static class EventTypes
{
    public const string ResourceCreated = "ResourceCreated";
    public const string ResourceUpdated = "ResourceUpdated";
    public const string ResourceEnabled = "ResourceEnabled";
    public const string ResourceDisabled = "ResourceDisabled";
    public const string MaintenanceSet = "MaintenanceSet";
    public const string ResourceDeleted = "ResourceDeleted";
    public const string ResourceStateChanged = "ResourceStateChanged";
    public const string HealthChanged = "HealthChanged";
    public const string ConnectivityChanged = "ConnectivityChanged";
    public const string AcquireSucceeded = "AcquireSucceeded";
    public const string AcquireFailed = "AcquireFailed";
    public const string HeartbeatLost = "HeartbeatLost";
    public const string LeaseExpired = "LeaseExpired";
    public const string LeaseReleased = "LeaseReleased";
    public const string LeaseForceReleased = "LeaseForceReleased";
    public const string AdminOverride = "AdminOverride";
    public const string CapabilityCreated = "CapabilityCreated";
    public const string AdapterCommandFailed = "AdapterCommandFailed";
    public const string SimulatorFaultSet = "SimulatorFaultSet";
    public const string StartupGraceApplied = "StartupGraceApplied";
}
