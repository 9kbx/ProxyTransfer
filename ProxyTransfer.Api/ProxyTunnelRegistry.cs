using System.Collections.Concurrent;
using System.Net;
using ProxyTransfer.Tunnel;

namespace ProxyTransfer.Api;

public sealed class ProxyTunnelRegistry : IAsyncDisposable
{
    private const string HttpDownstreamProtocol = "http";
    private const string Socks5DownstreamProtocol = "socks5";

    private readonly ConcurrentDictionary<Guid, TunnelEntry> _entries = new();
    private readonly ProxyTunnelHostOptions _defaults;

    public ProxyTunnelRegistry(ProxyTunnelHostOptions defaults)
    {
        _defaults = defaults;
    }

    public IReadOnlyList<ProxyTunnelResponse> List()
    {
        return _entries
            .Values.OrderByDescending(static entry => entry.CreatedAt)
            .Select(static entry => entry.ToResponse())
            .ToArray();
    }

    public IReadOnlyList<BatchSummaryResponse> ListBatches()
    {
        return _entries
            .Values.Where(static entry => !string.IsNullOrWhiteSpace(entry.BatchId))
            .GroupBy(static entry => entry.BatchId!)
            .Select(group => new BatchSummaryResponse(
                group.Key,
                group.Count(),
                group.Count(static entry => entry.Status == TunnelStatus.Running)
            ))
            .OrderByDescending(static batch => batch.BatchId)
            .ToArray();
    }

    public ProxyTunnelResponse? Get(Guid id)
    {
        return _entries.TryGetValue(id, out var entry) ? entry.ToResponse() : null;
    }

    public async Task<ImportProxiesResponse> ImportAsync(
        ImportProxiesRequest request,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request.ProxyText))
        {
            throw new ArgumentException("导入内容不能为空。", nameof(request));
        }

        var lines = request
            .ProxyText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (lines.Length == 0)
        {
            throw new InvalidOperationException(
                "没有可导入的代理。请按每行一个 HTTP 或 SOCKS5 代理填写。"
            );
        }

        var batchId = string.IsNullOrWhiteSpace(request.BatchId)
            ? $"batch-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}"
            : request.BatchId.Trim();

        var imported = new List<ProxyTunnelResponse>(lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            int? listenPort = request.FirstListenPort.HasValue
                ? request.FirstListenPort.Value + index
                : null;
            var created = await AddAsync(
                    new AddProxyRequest(
                        lines[index],
                        request.DownstreamProtocol,
                        request.Note,
                        batchId,
                        request.ListenAddress,
                        request.PublicHost,
                        listenPort,
                        request.AutoStart
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);

            imported.Add(created);
        }

        return new ImportProxiesResponse(batchId, imported.Count, imported);
    }

    public async Task<ProxyTunnelResponse> AddAsync(
        AddProxyRequest request,
        CancellationToken cancellationToken
    )
    {
        var remoteProxy = ProxyEndpoint.Parse(request.Proxy);
        var entry = new TunnelEntry(
            Guid.NewGuid(),
            string.IsNullOrWhiteSpace(request.BatchId) ? null : request.BatchId.Trim(),
            request.Note?.Trim(),
            remoteProxy,
            ResolveDownstreamProtocol(request.DownstreamProtocol, remoteProxy),
            ResolveListenAddress(request.ListenAddress),
            ResolvePublicHost(request.PublicHost),
            request.ListenPort ?? 0
        );

        if (!_entries.TryAdd(entry.Id, entry))
        {
            throw new InvalidOperationException("创建代理转发记录失败，请重试。");
        }

        if (request.AutoStart)
        {
            await StartEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        return entry.ToResponse();
    }

    public async Task<ProxyTunnelResponse?> StartAsync(
        Guid id,
        StartProxyRequest? request,
        CancellationToken cancellationToken
    )
    {
        if (!_entries.TryGetValue(id, out var entry))
        {
            return null;
        }

        if (request is not null)
        {
            entry.DownstreamProtocol = ResolveDownstreamProtocol(
                request.DownstreamProtocol,
                entry.RemoteProxy,
                entry.DownstreamProtocol
            );
            entry.ListenAddress = ResolveListenAddress(request.ListenAddress, entry.ListenAddress);
            entry.PublicHost = ResolvePublicHost(request.PublicHost, entry.PublicHost);
            entry.RequestedListenPort = request.ListenPort ?? entry.RequestedListenPort;
        }

        await StartEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        return entry.ToResponse();
    }

    public async Task<ProxyTunnelResponse?> StopAsync(Guid id)
    {
        if (!_entries.TryGetValue(id, out var entry))
        {
            return null;
        }

        await StopEntryAsync(entry).ConfigureAwait(false);
        return entry.ToResponse();
    }

    public async Task<int> StopBatchAsync(string batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId))
        {
            throw new ArgumentException("批次号不能为空。", nameof(batchId));
        }

        var targets = _entries
            .Values.Where(entry =>
                string.Equals(entry.BatchId, batchId, StringComparison.OrdinalIgnoreCase)
            )
            .ToArray();

        foreach (var entry in targets)
        {
            await StopEntryAsync(entry).ConfigureAwait(false);
        }

        return targets.Length;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _entries.Values)
        {
            await StopEntryAsync(entry).ConfigureAwait(false);
            entry.Dispose();
        }
    }

    private async Task StartEntryAsync(TunnelEntry entry, CancellationToken cancellationToken)
    {
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (entry.Tunnel is not null)
            {
                return;
            }

            try
            {
                var listenAddress = IPAddress.Parse(entry.ListenAddress);
                entry.Tunnel = await CreateTunnelAsync(entry, listenAddress, cancellationToken)
                    .ConfigureAwait(false);

                entry.ActiveListenPort = entry.Tunnel.LocalPort;
                entry.StartedAt = DateTimeOffset.UtcNow;
                entry.StoppedAt = null;
                entry.LastError = null;
                entry.Status = TunnelStatus.Running;
            }
            catch (Exception ex)
            {
                entry.Status = TunnelStatus.Error;
                entry.LastError = ex.Message;
                throw;
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private static async Task StopEntryAsync(TunnelEntry entry)
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
            if (entry.Status != TunnelStatus.Error)
            {
                entry.Status = TunnelStatus.Stopped;
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private string ResolveListenAddress(string? candidate, string? fallback = null)
    {
        var value = string.IsNullOrWhiteSpace(candidate)
            ? (string.IsNullOrWhiteSpace(fallback) ? _defaults.ListenAddress : fallback)
            : candidate.Trim();

        if (!IPAddress.TryParse(value, out _))
        {
            throw new FormatException($"监听地址无效: {value}");
        }

        return value;
    }

    private string ResolvePublicHost(string? candidate, string? fallback = null)
    {
        var value = string.IsNullOrWhiteSpace(candidate)
            ? (string.IsNullOrWhiteSpace(fallback) ? _defaults.PublicHost : fallback)
            : candidate.Trim();

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException("对外访问主机不能为空。请填写公网 IP 或域名。");
        }

        return value;
    }

    private static string ResolveDownstreamProtocol(
        string? candidate,
        ProxyEndpoint remoteProxy,
        string? fallback = null
    )
    {
        var value = string.IsNullOrWhiteSpace(candidate)
            ? (string.IsNullOrWhiteSpace(fallback) ? HttpDownstreamProtocol : fallback)
            : candidate.Trim().ToLowerInvariant();

        if (value == HttpDownstreamProtocol)
        {
            return value;
        }

        if (value == Socks5DownstreamProtocol)
        {
            return value;
        }

        throw new FormatException($"不支持的下游出口协议: {candidate}");
    }

    private static Task<IProxyTunnel> CreateTunnelAsync(
        TunnelEntry entry,
        IPAddress listenAddress,
        CancellationToken cancellationToken
    )
    {
        return entry.DownstreamProtocol switch
        {
            HttpDownstreamProtocol => CreateHttpTunnelAsync(
                entry,
                listenAddress,
                cancellationToken
            ),
            Socks5DownstreamProtocol => CreateSocks5TunnelAsync(
                entry,
                listenAddress,
                cancellationToken
            ),
            _ => throw new InvalidOperationException(
                $"未知的下游出口协议: {entry.DownstreamProtocol}"
            ),
        };
    }

    private static async Task<IProxyTunnel> CreateHttpTunnelAsync(
        TunnelEntry entry,
        IPAddress listenAddress,
        CancellationToken cancellationToken
    )
    {
        return await HttpProxyTunnel
            .StartAsync(
                entry.RemoteProxy,
                listenAddress,
                entry.RequestedListenPort,
                entry.PublicHost,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task<IProxyTunnel> CreateSocks5TunnelAsync(
        TunnelEntry entry,
        IPAddress listenAddress,
        CancellationToken cancellationToken
    )
    {
        return await Socks5ProxyTunnel
            .StartAsync(
                entry.RemoteProxy,
                listenAddress,
                entry.RequestedListenPort,
                entry.PublicHost,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private enum TunnelStatus
    {
        Created,
        Running,
        Stopped,
        Error,
    }

    private sealed class TunnelEntry : IDisposable
    {
        public TunnelEntry(
            Guid id,
            string? batchId,
            string? note,
            ProxyEndpoint remoteProxy,
            string downstreamProtocol,
            string listenAddress,
            string publicHost,
            int requestedListenPort
        )
        {
            Id = id;
            BatchId = batchId;
            Note = note;
            RemoteProxy = remoteProxy;
            DownstreamProtocol = downstreamProtocol;
            ListenAddress = listenAddress;
            PublicHost = publicHost;
            RequestedListenPort = requestedListenPort;
            CreatedAt = DateTimeOffset.UtcNow;
            Status = TunnelStatus.Created;
        }

        public Guid Id { get; }

        public string? BatchId { get; }

        public string? Note { get; }

        public ProxyEndpoint RemoteProxy { get; }

        public string DownstreamProtocol { get; set; }

        public DateTimeOffset CreatedAt { get; }

        public DateTimeOffset? StartedAt { get; set; }

        public DateTimeOffset? StoppedAt { get; set; }

        public string ListenAddress { get; set; }

        public string PublicHost { get; set; }

        public int RequestedListenPort { get; set; }

        public int ActiveListenPort { get; set; }

        public TunnelStatus Status { get; set; }

        public string? LastError { get; set; }

        public IProxyTunnel? Tunnel { get; set; }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public ProxyTunnelResponse ToResponse()
        {
            var forwardedPort = Tunnel?.LocalPort ?? ActiveListenPort;
            var forwardedProxy = forwardedPort > 0 ? BuildForwardedProxy(forwardedPort) : null;

            return new ProxyTunnelResponse(
                Id,
                BatchId,
                Note,
                RemoteProxy.ProxyUri,
                RemoteProxy.SafeDisplayUri,
                DownstreamProtocol,
                ListenAddress,
                PublicHost,
                RequestedListenPort,
                forwardedPort,
                forwardedProxy,
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
            var scheme =
                DownstreamProtocol == Socks5DownstreamProtocol
                    ? Socks5DownstreamProtocol
                    : HttpDownstreamProtocol;

            return $"{scheme}://{FormatHost(PublicHost)}:{forwardedPort}";
        }

        private static string FormatHost(string host) =>
            host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
                ? $"[{host}]"
                : host;
    }
}
