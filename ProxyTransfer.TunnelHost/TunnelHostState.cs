namespace ProxyTransfer.TunnelHost;

public sealed class TunnelHostState
{
    public List<TunnelInstanceDocument> Instances { get; init; } = [];
}

public sealed class TunnelInstanceDocument
{
    public Guid Id { get; set; }

    public string Kind { get; set; } = TunnelKinds.Direct;

    public string? Note { get; set; }

    public string? BatchId { get; set; }

    public string? PoolId { get; set; }

    public string DownstreamProtocol { get; set; } = TunnelProtocols.Http;

    public string ListenAddress { get; set; } = "0.0.0.0";

    public string PublicHost { get; set; } = "127.0.0.1";

    public int RequestedListenPort { get; set; }

    public int LastActiveListenPort { get; set; }

    public bool DesiredRunning { get; set; }

    public string? RemoteProxy { get; set; }

    public List<string> Upstreams { get; init; } = [];

    public string? SelectionPolicy { get; set; }

    public int? StickyMinutes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? StoppedAt { get; set; }

    public string? LastError { get; set; }
}

public static class TunnelKinds
{
    public const string Direct = "direct";
    public const string Pool = "pool";
}

public static class TunnelProtocols
{
    public const string Http = "http";
    public const string Socks5 = "socks5";
}

public static class TunnelSelectionPolicies
{
    public const string Sticky = "sticky";
    public const string RoundRobin = "round-robin";
    public const string LeastFailures = "least-failures";
}

public static class TunnelStatuses
{
    public const string Stopped = "Stopped";
    public const string Running = "Running";
    public const string Error = "Error";
}
