namespace ProxyTransfer.Api;

public sealed class ApiKeyAuthOptions
{
    public const string SectionName = "Auth";

    public string ApiKey { get; set; } = string.Empty;
}
