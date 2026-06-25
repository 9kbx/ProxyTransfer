namespace ProxyTransfer.TunnelHost;

public sealed class TunnelHostOptions
{
    public const string SectionName = "TunnelHost";

    public string NodeId { get; set; } = Environment.MachineName;

    public string ListenAddress { get; set; } = "0.0.0.0";

    public string PublicHost { get; set; } = "127.0.0.1";

    public int? ListenPortRangeStart { get; set; }

    public int? ListenPortRangeEnd { get; set; }

    public string ManagementUrl { get; set; } = "http://0.0.0.0:5081";

    public string StateFilePath { get; set; } = "App_Data/tunnel-host-state.json";

    public string? ManagementApiKey { get; set; }

    public int DefaultStickyMinutes { get; set; } = 10;

    public int FailureCooldownSeconds { get; set; } = 90;
}
