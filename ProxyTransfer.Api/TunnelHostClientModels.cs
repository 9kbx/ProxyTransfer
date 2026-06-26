namespace ProxyTransfer.Api;

internal sealed record TunnelHostCreateDirectRequest(
    string RemoteProxy,
    string? DownstreamProtocol,
    string? Note,
    string? BatchId,
    string? ListenAddress,
    string? PublicHost,
    int? ListenPort,
    bool AutoStart = true
);

internal sealed record TunnelHostCreatePoolRequest(
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

internal sealed record TunnelHostStartRequest(
    string? DownstreamProtocol,
    string? ListenAddress,
    string? PublicHost,
    int? ListenPort,
    IReadOnlyList<string>? Upstreams,
    string? SelectionPolicy,
    int? StickyMinutes
);

internal sealed record TunnelHostInstanceResponse(
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

internal sealed record TunnelHostPortRangeResponse(int? StartPort, int? EndPort, string? Message);
