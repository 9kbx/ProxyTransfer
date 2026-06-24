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
