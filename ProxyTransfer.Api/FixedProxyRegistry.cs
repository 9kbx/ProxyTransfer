using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Hosting;
using ProxyTransfer.Tunnel;

namespace ProxyTransfer.Api;

public sealed class FixedProxyRegistry : IAsyncDisposable
{
    private const string HttpDownstreamProtocol = "http";
    private const string Socks5DownstreamProtocol = "socks5";
    private const string SelectionPolicy = "sticky";

    private readonly ConcurrentDictionary<Guid, FixedProxyEntry> _entries = new();
    private readonly ProxyTunnelHostOptions _options;
    private readonly UpstreamPoolRegistry _upstreamPools;

    public FixedProxyRegistry(ProxyTunnelHostOptions options, UpstreamPoolRegistry upstreamPools)
    {
        _options = options;
        _upstreamPools = upstreamPools;
    }

    public IReadOnlyList<FixedProxyResponse> List()
    {
        return _entries
            .Values.OrderByDescending(static entry => entry.CreatedAt)
            .Select(entry => entry.ToResponse(_upstreamPools.GetPoolSnapshot(entry.PoolId)))
            .ToArray();
    }

    public FixedProxyResponse? Get(Guid id)
    {
        if (!_entries.TryGetValue(id, out var entry))
        {
            return null;
        }

        return entry.ToResponse(_upstreamPools.GetPoolSnapshot(entry.PoolId));
    }

    public async Task<FixedProxyResponse> AddAsync(
        FixedProxyRequest request,
        CancellationToken cancellationToken
    )
    {
        var pool = _upstreamPools.GetPoolSnapshot(request.PoolId);
        var entry = new FixedProxyEntry(
            Guid.NewGuid(),
            pool.PoolId,
            request.Note?.Trim(),
            ResolveDownstreamProtocol(request.DownstreamProtocol),
            ResolveListenAddress(request.ListenAddress),
            ResolvePublicHost(request.PublicHost),
            request.ListenPort ?? 0,
            ResolveStickyMinutes(request.StickyMinutes)
        );

        if (!_entries.TryAdd(entry.Id, entry))
        {
            throw new InvalidOperationException("创建固定代理入口失败，请重试。");
        }

        if (request.AutoStart)
        {
            await StartEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        return entry.ToResponse(pool);
    }

    public async Task<FixedProxyResponse?> StartAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!_entries.TryGetValue(id, out var entry))
        {
            return null;
        }

        await StartEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        return entry.ToResponse(_upstreamPools.GetPoolSnapshot(entry.PoolId));
    }

    public async Task<FixedProxyResponse?> StopAsync(Guid id)
    {
        if (!_entries.TryGetValue(id, out var entry))
        {
            return null;
        }

        await StopEntryAsync(entry).ConfigureAwait(false);
        return entry.ToResponse(_upstreamPools.GetPoolSnapshot(entry.PoolId));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _entries.Values)
        {
            await StopEntryAsync(entry).ConfigureAwait(false);
            entry.Dispose();
        }
    }

    private async Task StartEntryAsync(FixedProxyEntry entry, CancellationToken cancellationToken)
    {
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (entry.Tunnel is not null)
            {
                return;
            }

            _upstreamPools.GetPoolSnapshot(entry.PoolId);

            try
            {
                var listenAddress = IPAddress.Parse(entry.ListenAddress);
                var router = new StickyUpstreamRouter(entry, _upstreamPools);
                entry.Tunnel = await CreateTunnelAsync(
                        entry,
                        router,
                        listenAddress,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                entry.ActiveListenPort = entry.Tunnel.LocalPort;
                entry.StartedAt = DateTimeOffset.UtcNow;
                entry.StoppedAt = null;
                entry.LastError = null;
                entry.Status = FixedProxyStatus.Running;
            }
            catch (Exception ex)
            {
                entry.Status = FixedProxyStatus.Error;
                entry.LastError = ex.Message;
                throw;
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private static async Task StopEntryAsync(FixedProxyEntry entry)
    {
        await entry.Gate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (entry.Tunnel is not null)
            {
                await entry.Tunnel.DisposeAsync().ConfigureAwait(false);
                entry.Tunnel = null;
            }

            entry.ActiveListenPort = 0;
            entry.StoppedAt = DateTimeOffset.UtcNow;
            if (entry.Status != FixedProxyStatus.Error)
            {
                entry.Status = FixedProxyStatus.Stopped;
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private static Task<IProxyTunnel> CreateTunnelAsync(
        FixedProxyEntry entry,
        IUpstreamRouter router,
        IPAddress listenAddress,
        CancellationToken cancellationToken
    )
    {
        return entry.DownstreamProtocol switch
        {
            HttpDownstreamProtocol => CreateHttpTunnelAsync(
                entry,
                router,
                listenAddress,
                cancellationToken
            ),
            Socks5DownstreamProtocol => CreateSocks5TunnelAsync(
                entry,
                router,
                listenAddress,
                cancellationToken
            ),
            _ => throw new InvalidOperationException(
                $"未知的下游出口协议: {entry.DownstreamProtocol}"
            ),
        };
    }

    private static async Task<IProxyTunnel> CreateHttpTunnelAsync(
        FixedProxyEntry entry,
        IUpstreamRouter router,
        IPAddress listenAddress,
        CancellationToken cancellationToken
    )
    {
        return await DynamicHttpProxyTunnel
            .StartAsync(
                router,
                listenAddress,
                entry.RequestedListenPort,
                entry.PublicHost,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task<IProxyTunnel> CreateSocks5TunnelAsync(
        FixedProxyEntry entry,
        IUpstreamRouter router,
        IPAddress listenAddress,
        CancellationToken cancellationToken
    )
    {
        return await DynamicSocks5ProxyTunnel
            .StartAsync(
                router,
                listenAddress,
                entry.RequestedListenPort,
                entry.PublicHost,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private string ResolveListenAddress(string? candidate)
    {
        var value = string.IsNullOrWhiteSpace(candidate)
            ? _options.ListenAddress
            : candidate.Trim();
        if (!IPAddress.TryParse(value, out _))
        {
            throw new FormatException($"监听地址无效: {value}");
        }

        return value;
    }

    private string ResolvePublicHost(string? candidate)
    {
        var value = string.IsNullOrWhiteSpace(candidate) ? _options.PublicHost : candidate.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException("对外访问主机不能为空。请填写公网 IP 或域名。");
        }

        return value;
    }

    private string ResolveDownstreamProtocol(string? candidate)
    {
        var value = string.IsNullOrWhiteSpace(candidate)
            ? HttpDownstreamProtocol
            : candidate.Trim().ToLowerInvariant();

        return value switch
        {
            HttpDownstreamProtocol => value,
            Socks5DownstreamProtocol => value,
            _ => throw new FormatException($"不支持的下游出口协议: {candidate}"),
        };
    }

    private int ResolveStickyMinutes(int? candidate)
    {
        var value = candidate ?? _options.DefaultStickyMinutes;
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candidate), "粘性会话分钟数必须大于 0。");
        }

        return value;
    }

    private enum FixedProxyStatus
    {
        Created,
        Running,
        Stopped,
        Error,
    }

    private sealed class FixedProxyEntry : IDisposable
    {
        public FixedProxyEntry(
            Guid id,
            string poolId,
            string? note,
            string downstreamProtocol,
            string listenAddress,
            string publicHost,
            int requestedListenPort,
            int stickyMinutes
        )
        {
            Id = id;
            PoolId = poolId;
            Note = note;
            DownstreamProtocol = downstreamProtocol;
            ListenAddress = listenAddress;
            PublicHost = publicHost;
            RequestedListenPort = requestedListenPort;
            StickyMinutes = stickyMinutes;
            CreatedAt = DateTimeOffset.UtcNow;
            Status = FixedProxyStatus.Created;
        }

        public Guid Id { get; }

        public string PoolId { get; }

        public string? Note { get; }

        public string DownstreamProtocol { get; }

        public string ListenAddress { get; }

        public string PublicHost { get; }

        public int RequestedListenPort { get; }

        public int ActiveListenPort { get; set; }

        public int StickyMinutes { get; }

        public DateTimeOffset CreatedAt { get; }

        public DateTimeOffset? StartedAt { get; set; }

        public DateTimeOffset? StoppedAt { get; set; }

        public FixedProxyStatus Status { get; set; }

        public string? LastError { get; set; }

        public IProxyTunnel? Tunnel { get; set; }

        public UpstreamLease? LastLease { get; set; }

        public DateTimeOffset? LastLeaseExpiresAt { get; set; }

        public string? LastSelectedUpstreamDisplay { get; set; }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public FixedProxyResponse ToResponse(UpstreamPoolSnapshot snapshot)
        {
            var forwardedPort = Tunnel?.LocalPort ?? ActiveListenPort;
            var forwardedProxy = forwardedPort > 0 ? BuildForwardedProxy(forwardedPort) : null;

            return new FixedProxyResponse(
                Id,
                PoolId,
                Note,
                DownstreamProtocol,
                ListenAddress,
                PublicHost,
                RequestedListenPort,
                forwardedPort,
                forwardedProxy,
                SelectionPolicy,
                StickyMinutes,
                snapshot.TotalCount,
                snapshot.HealthyCount,
                LastLease?.Endpoint.ProxyUri,
                LastSelectedUpstreamDisplay,
                Status.ToString(),
                CreatedAt,
                StartedAt,
                StoppedAt,
                LastError
            );
        }

        public void Dispose()
        {
            Gate.Dispose();
        }

        private string BuildForwardedProxy(int forwardedPort)
        {
            var scheme = DownstreamProtocol == Socks5DownstreamProtocol ? "socks5" : "http";
            return $"{scheme}://{FormatHost(PublicHost)}:{forwardedPort}";
        }

        private static string FormatHost(string host) =>
            host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
                ? $"[{host}]"
                : host;
    }

    private sealed class StickyUpstreamRouter : IUpstreamRouter
    {
        private readonly FixedProxyEntry _entry;
        private readonly UpstreamPoolRegistry _upstreamPools;

        public StickyUpstreamRouter(FixedProxyEntry entry, UpstreamPoolRegistry upstreamPools)
        {
            _entry = entry;
            _upstreamPools = upstreamPools;
        }

        public ValueTask<UpstreamLease> SelectAsync(
            ProxyConnectRequest request,
            CancellationToken cancellationToken
        )
        {
            _ = request;
            _ = cancellationToken;

            if (
                _entry.LastLease is { } lastLease
                && _entry.LastLeaseExpiresAt is { } expiresAt
                && expiresAt > DateTimeOffset.UtcNow
                && _upstreamPools.IsLeaseHealthy(_entry.PoolId, lastLease.UpstreamId)
            )
            {
                return ValueTask.FromResult(lastLease);
            }

            var lease = _upstreamPools.Acquire(_entry.PoolId);
            _entry.LastLease = lease;
            _entry.LastLeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(_entry.StickyMinutes);
            _entry.LastSelectedUpstreamDisplay = _upstreamPools.GetSafeDisplay(lease.UpstreamId);
            return ValueTask.FromResult(lease);
        }

        public ValueTask ReportSuccessAsync(
            UpstreamLease lease,
            CancellationToken cancellationToken
        )
        {
            _ = cancellationToken;
            _upstreamPools.MarkSuccess(_entry.PoolId, lease.UpstreamId);
            return ValueTask.CompletedTask;
        }

        public ValueTask ReportFailureAsync(
            UpstreamLease lease,
            Exception exception,
            CancellationToken cancellationToken
        )
        {
            _ = cancellationToken;
            _upstreamPools.MarkFailure(_entry.PoolId, lease.UpstreamId, exception);

            if (_entry.LastLease?.UpstreamId == lease.UpstreamId)
            {
                _entry.LastLease = null;
                _entry.LastLeaseExpiresAt = null;
            }

            return ValueTask.CompletedTask;
        }
    }
}
