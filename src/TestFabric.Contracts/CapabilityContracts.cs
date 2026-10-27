namespace TestFabric.Contracts;

/// <summary>
/// 能力定义。Schema 描述该能力允许的属性名与类型，
/// 用于注册期校验（防止属性名拼错导致 Discovery 静默不匹配）与请求期校验（UNSUPPORTED_CONSTRAINT）。
/// </summary>
public record CapabilityDto(string CapabilityId, int Version, CapabilitySchema Schema);

public record CapabilitySchema(Dictionary<string, CapabilityPropertyDef> Properties);

public record CapabilityPropertyDef(string Type)
{
    public const string Number = "number";
    public const string Boolean = "boolean";
    public const string String = "string";
    public const string Array = "array";
}

public record CreateCapabilityRequest(string CapabilityId, int Version, CapabilitySchema Schema);
