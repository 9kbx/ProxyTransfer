namespace ProxyTransfer.Api;

public sealed class UpstreamPoolState
{
    public List<UpstreamPoolStateItemDocument> Pools { get; init; } = [];
}

public sealed record UpstreamPoolStateItemDocument(
    string PoolId,
    string? Note,
    int NextIndex,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<UpstreamProxyStateItemDocument> Items
);

public sealed record UpstreamProxyStateItemDocument(
    Guid Id,
    string Proxy,
    string Status,
    int FailureCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    DateTimeOffset? DisabledUntil,
    string? LastError
);
