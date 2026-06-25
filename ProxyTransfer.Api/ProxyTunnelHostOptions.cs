namespace ProxyTransfer.Api;

public sealed class ProxyTunnelHostOptions
{
    public string ManagementUrl { get; set; } = "http://127.0.0.1:5081";

    public string? ManagementApiKey { get; set; }

    public string? ApiKey { get; set; }

    public string ListenAddress { get; set; } = "0.0.0.0";

    public string PublicHost { get; set; } = "127.0.0.1";

    public string ApiUrl { get; set; } = "http://0.0.0.0:5080";

    public int DefaultStickyMinutes { get; set; } = 10;

    public int FailureCooldownSeconds { get; set; } = 90;

    public int ProbeIntervalSeconds { get; set; } = 60;

    public int ProbeTimeoutSeconds { get; set; } = 10;

    public string ProbeTargetHost { get; set; } = "example.com";

    public int ProbeTargetPort { get; set; } = 443;

    public string TestHistoryFilePath { get; set; } = "App_Data/test-history.json";

    public string UpstreamPoolStateFilePath { get; set; } = "App_Data/upstream-pools.json";

    public string UpstreamPoolTestHistoryFilePath { get; set; } =
        "App_Data/upstream-pool-test-history.json";
}
