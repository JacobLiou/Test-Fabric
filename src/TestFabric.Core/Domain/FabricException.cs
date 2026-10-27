using TestFabric.Contracts;

namespace TestFabric.Core.Domain;

/// <summary>领域/基础设施异常，携带平台统一错误码，由 API 层映射为 problem+json。</summary>
public class FabricException : Exception
{
    public string Code { get; }
    public Dictionary<string, object?> Details { get; }

    public FabricException(string code, string message, Dictionary<string, object?>? details = null)
        : base(message)
    {
        Code = code;
        Details = details ?? [];
    }

    public static FabricException Validation(string message, Dictionary<string, object?>? details = null)
        => new(ErrorCodes.ValidationFailed, message, details);

    public static FabricException NotFound(string code, string message)
        => new(code, message);
}
