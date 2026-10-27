namespace TestFabric.Contracts;

/// <summary>平台统一错误码。API 层负责映射为 HTTP 状态码与 RFC9457 problem+json 响应体。</summary>
public static class ErrorCodes
{
    // 4xx
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string UnsupportedConstraint = "UNSUPPORTED_CONSTRAINT";
    public const string ReasonRequired = "REASON_REQUIRED";

    // 401/403
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string LeaseOwnerMismatch = "LEASE_OWNER_MISMATCH";

    // 404
    public const string ResourceNotFound = "RESOURCE_NOT_FOUND";
    public const string LeaseNotFound = "LEASE_NOT_FOUND";
    public const string CapabilityNotFound = "CAPABILITY_NOT_FOUND";

    // 409
    public const string ResourceIdDuplicated = "RESOURCE_ID_DUPLICATED";
    public const string ResourceBusy = "RESOURCE_BUSY";
    public const string NoResourceAvailable = "NO_RESOURCE_AVAILABLE";
    public const string LeaseExpired = "LEASE_EXPIRED";
    public const string LeaseRequired = "LEASE_REQUIRED";
    public const string IdempotencyKeyConflict = "IDEMPOTENCY_KEY_CONFLICT";
    public const string StateConflict = "STATE_CONFLICT";
    public const string AdapterNotCompatible = "ADAPTER_NOT_COMPATIBLE";
    public const string CommandCancelled = "COMMAND_CANCELLED";

    // 5xx
    public const string AdapterUnavailable = "ADAPTER_UNAVAILABLE";
    public const string AdapterCommandFailed = "ADAPTER_COMMAND_FAILED";

    public static int ToHttpStatus(string code) => code switch
    {
        ValidationFailed or UnsupportedConstraint or ReasonRequired => 400,
        Unauthorized => 401,
        Forbidden or LeaseOwnerMismatch => 403,
        ResourceNotFound or LeaseNotFound or CapabilityNotFound => 404,
        ResourceIdDuplicated or ResourceBusy or NoResourceAvailable or LeaseExpired or LeaseRequired
            or IdempotencyKeyConflict or StateConflict or AdapterNotCompatible or CommandCancelled => 409,
        _ => 500,
    };
}
