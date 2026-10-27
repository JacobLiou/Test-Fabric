namespace TestFabric.Core.Domain;

public class Resource
{
    public string ResourceId { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string AssetTag { get; set; } = "";
    public string AddressType { get; set; } = "";
    public string Address { get; set; } = "";
    public string AdapterId { get; set; } = "";
    public string AdapterVersion { get; set; } = "0.0.0";
    public string Location { get; set; } = "";
    public string Description { get; set; } = "";
    public string AdministrativeState { get; set; } = States.Administrative.Enabled;
    public string OperationalState { get; set; } = States.Operational.Unknown;
    public string HealthState { get; set; } = States.Health.Unknown;
    public string ConnectivityState { get; set; } = States.Connectivity.Unknown;
    public DateTimeOffset? CalibrationDueDate { get; set; }
    public DateTimeOffset? LastReleasedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>资源在某能力下声明的属性（properties JSON 反序列化结果）。</summary>
    public Dictionary<string, object?>? CapabilityProperties { get; set; }

    public bool IsAllocatable => AdministrativeState == States.Administrative.Enabled
        && OperationalState == States.Operational.Available
        && ConnectivityState == States.Connectivity.Online
        && HealthState == States.Health.Healthy
        && DeletedAt is null;

    public string CalibrationState(TimeProvider time)
    {
        if (CalibrationDueDate is null) return Contracts.CalibrationInfo.Unknown;
        var now = time.GetUtcNow().UtcDateTime;
        if (now > CalibrationDueDate.Value.UtcDateTime) return Contracts.CalibrationInfo.Expired;
        if (CalibrationDueDate.Value.UtcDateTime - now <= TimeSpan.FromDays(30)) return Contracts.CalibrationInfo.Expiring;
        return Contracts.CalibrationInfo.Valid;
    }
}

public class Lease
{
    public string LeaseId { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string CapabilityId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string? IdempotencyKey { get; set; }
    public string? RequestHash { get; set; }
    public string WorkOrder { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string Operation { get; set; } = "";
    public string Status { get; set; } = States.LeaseStatus.Active;
    public int TtlSeconds { get; set; }
    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? EndReason { get; set; }
}
