namespace ProxyTransfer.TunnelHost;

public sealed record CreateDirectTunnelRequest(
    string RemoteProxy,
    string? DownstreamProtocol,
    string? Note,
    string? BatchId,
    string? ListenAddress,
    string? PublicHost,
    int? ListenPort,
    bool AutoStart = true
);

public sealed record CreatePoolTunnelRequest(
    string PoolId,
    IReadOnlyList<string> Upstreams,
    string? DownstreamProtocol,
    string? Note,
    string? ListenAddress,
    string? PublicHost,
    int? ListenPort,
    string? SelectionPolicy,
    int? StickyMinutes,
    bool AutoStart = true
);

public sealed record StartTunnelRequest(
    string? DownstreamProtocol,
    string? ListenAddress,
    string? PublicHost,
    int? ListenPort,
    IReadOnlyList<string>? Upstreams,
    string? SelectionPolicy,
    int? StickyMinutes
);

public sealed record TunnelHostStatusResponse(
    string NodeId,
    string ManagementUrl,
    int TotalCount,
    int RunningCount,
    DateTimeOffset StartedAt
);

public sealed record TunnelInstanceResponse(
    Guid Id,
    string NodeId,
    string Kind,
    string? Note,
    string? BatchId,
    string? PoolId,
    string DownstreamProtocol,
    string ListenAddress,
    string PublicHost,
    int RequestedListenPort,
    int ActiveListenPort,
    bool DesiredRunning,
    string? RemoteProxy,
    string? RemoteProxyDisplay,
    IReadOnlyList<string> Upstreams,
    int HealthyUpstreamCount,
    string? SelectionPolicy,
    int? StickyMinutes,
    string? ForwardedProxy,
    string? LastSelectedUpstream,
    string? LastSelectedUpstreamDisplay,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? StoppedAt,
    string? LastError
);
