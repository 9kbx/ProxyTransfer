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

public sealed record BatchTunnelTestRequest(string BatchId, bool RunningOnly = true);

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

public sealed record UpdateUpstreamPoolRequest(string ProxyText, string? Note);

public sealed record DeleteUpstreamPoolProxiesRequest(
    IReadOnlyList<Guid>? UpstreamIds = null,
    string? ProxyText = null,
    bool RemoveFailed = false
);

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

public sealed record DeleteUpstreamPoolProxiesResponse(
    string PoolId,
    int RemovedCount,
    int RemainingCount,
    IReadOnlyList<UpstreamProxyResponse> Items
);

public sealed record UpstreamPoolTestRequest(
    Guid? UpstreamId = null,
    IReadOnlyList<Guid>? UpstreamIds = null
);

public sealed record UpstreamProxyTestItemResponse(
    Guid UpstreamId,
    string ProxyDisplay,
    bool Success,
    string? ExitIp,
    long? ElapsedMilliseconds,
    string? ErrorMessage,
    DateTimeOffset TestedAt
);

public sealed record UpstreamPoolTestResponse(
    Guid RunId,
    DateTimeOffset CompletedAt,
    string PoolId,
    int TotalCount,
    int SuccessCount,
    int FailureCount,
    IReadOnlyList<UpstreamProxyTestItemResponse> Items
);

public sealed record FixedProxyRequest(
    string PoolId,
    string? DownstreamProtocol,
    string? Note,
    string? ListenAddress,
    string? PublicHost,
    int? ListenPort,
    string? SelectionPolicy,
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

public sealed record ProxyTestRequest(int? IterationCount, int? IntervalSeconds);

public sealed record ProxyTestLogEntry(DateTimeOffset Timestamp, string Level, string Message);

public sealed record ProxyTestSwitchSummary(
    bool HasExitIpSwitch,
    bool HasUpstreamSwitch,
    int ExitIpSwitchCount,
    int UpstreamSwitchCount,
    int UniqueExitIpCount,
    int UniqueUpstreamCount,
    int SuccessfulObservationCount
);

public sealed record ProxyTestResponse(
    Guid RunId,
    DateTimeOffset CompletedAt,
    string Mode,
    Guid ResourceId,
    string ProxyDisplay,
    string? ForwardedProxy,
    bool Success,
    int SuccessCount,
    int FailureCount,
    string? LastExitIp,
    string? LastSelectedUpstreamDisplay,
    ProxyTestSwitchSummary? SwitchSummary,
    IReadOnlyList<ProxyTestLogEntry> Logs
);

public sealed record BatchTunnelTestItemResponse(
    Guid TunnelId,
    string ProxyDisplay,
    string? ForwardedProxy,
    string Status,
    bool Success,
    string? ErrorMessage,
    Guid? RunId,
    DateTimeOffset? CompletedAt
);

public sealed record BatchTunnelTestResponse(
    string BatchId,
    int TotalCount,
    int TestedCount,
    int SuccessCount,
    int FailureCount,
    IReadOnlyList<BatchTunnelTestItemResponse> Items
);
