namespace TestFabric.Contracts;

public record CreateResourceRequest(
    string ResourceId,
    string ResourceType,
    string Manufacturer,
    string Model,
    string AddressType,
    string Address,
    string AdapterId,
    string Location,
    string? SerialNumber = null,
    string? AssetTag = null,
    string? Description = null,
    string? CalibrationDueDate = null,
    List<CapabilityAssignment>? Capabilities = null);

public record UpdateResourceRequest(
    string? Manufacturer = null,
    string? Model = null,
    string? AddressType = null,
    string? Address = null,
    string? Location = null,
    string? SerialNumber = null,
    string? AssetTag = null,
    string? Description = null,
    string? CalibrationDueDate = null);

/// <summary>资源与能力的关联，properties 必须符合能力 schema。</summary>
public record CapabilityAssignment(string CapabilityId, Dictionary<string, object?> Properties);

public record ResourceDto(
    string ResourceId,
    string ResourceType,
    string Manufacturer,
    string Model,
    string AddressType,
    string Address,
    string AdapterId,
    string AdapterVersion,
    string Location,
    string? SerialNumber,
    string? AssetTag,
    string? Description,
    string AdministrativeState,
    string OperationalState,
    string HealthState,
    string ConnectivityState,
    string CalibrationState,
    DateTimeOffset? CalibrationDueDate,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    LeaseSummaryDto? CurrentLease,
    List<ResourceCapabilityDto> Capabilities);

public record ResourceCapabilityDto(string CapabilityId, Dictionary<string, object?> Properties);

public record LeaseSummaryDto(
    string LeaseId,
    string ClientId,
    string? WorkOrder,
    string? SerialNumber,
    string? Operation,
    DateTimeOffset ExpiresAt,
    int TtlSeconds);

public record CalibrationInfo(string State, DateTimeOffset? DueDate)
{
    public const string Valid = "Valid";
    public const string Expiring = "Expiring";
    public const string Expired = "Expired";
    public const string Unknown = "Unknown";
}

public record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Offset, int Limit);
