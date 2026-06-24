namespace ProxyTransfer.Api;

public sealed record AddProxyRequest(
    string Proxy,
    string? DownstreamProtocol,
    string? Note,
    string? BatchId,
    string? ListenAddress,
    string? PublicHost,
    int? ListenPort,
    bool AutoStart = true
);

public sealed record ImportProxiesRequest(
    string ProxyText,
    string? DownstreamProtocol,
    string? BatchId,
    string? Note,
    string? ListenAddress,
    string? PublicHost,
    int? FirstListenPort,
    bool AutoStart = true
);

public sealed record StartProxyRequest(
    string? DownstreamProtocol,
    string? ListenAddress,
    string? PublicHost,
    int? ListenPort
);

public sealed record StopBatchRequest(string BatchId);

public sealed record ProxyTunnelResponse(
    Guid Id,
    string? BatchId,
    string? Note,
    string RemoteProxy,
    string RemoteProxyDisplay,
    string DownstreamProtocol,
    string ListenAddress,
    string PublicHost,
    int RequestedListenPort,
    int ActiveListenPort,
    string? ForwardedProxy,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? StoppedAt,
    string? LastError
);

public sealed record ImportProxiesResponse(
    string BatchId,
    int ImportedCount,
    IReadOnlyList<ProxyTunnelResponse> Items
);

public sealed record BatchSummaryResponse(string BatchId, int TotalCount, int RunningCount);

public sealed record ImportUpstreamPoolRequest(string ProxyText, string? PoolId, string? Note);

public sealed record UpstreamProxyResponse(
    Guid Id,
    string PoolId,
    string Proxy,
    string ProxyDisplay,
    string Status,
    int FailureCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    DateTimeOffset? DisabledUntil,
    string? LastError
);

public sealed record UpstreamPoolResponse(
    string PoolId,
    string? Note,
    int TotalCount,
    int HealthyCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public sealed record UpstreamPoolDetailsResponse(
    string PoolId,
    string? Note,
    int TotalCount,
    int HealthyCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<UpstreamProxyResponse> Items
);

public sealed record ImportUpstreamPoolResponse(
    string PoolId,
    int ImportedCount,
    int TotalCount,
    IReadOnlyList<UpstreamProxyResponse> Items
);

public sealed record FixedProxyRequest(
    string PoolId,
    string? DownstreamProtocol,
    string? Note,
    string? ListenAddress,
    string? PublicHost,
    int? ListenPort,
    int? StickyMinutes,
    bool AutoStart = true
);

public sealed record FixedProxyResponse(
    Guid Id,
    string PoolId,
    string? Note,
    string DownstreamProtocol,
    string ListenAddress,
    string PublicHost,
    int RequestedListenPort,
    int ActiveListenPort,
    string? ForwardedProxy,
    string SelectionPolicy,
    int StickyMinutes,
    int TotalUpstreamCount,
    int HealthyUpstreamCount,
    string? LastSelectedUpstream,
    string? LastSelectedUpstreamDisplay,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? StoppedAt,
    string? LastError
);
