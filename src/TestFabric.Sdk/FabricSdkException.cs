namespace TestFabric.Sdk;

/// <summary>SDK 侧异常：从 problem+json 解析出的平台错误码。</summary>
public class FabricSdkException : Exception
{
    public string Code { get; }
    public int HttpStatusCode { get; }

    public FabricSdkException(string code, string message, int httpStatusCode = 0)
        : base($"[{code}] {message}")
    {
        Code = code;
        HttpStatusCode = httpStatusCode;
    }
}
