using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;
using ProxyTransfer.Tunnel;

namespace ProxyTransfer.TunnelHost;

public sealed class TunnelHostRegistry
{
    private readonly ConcurrentDictionary<Guid, TunnelRuntimeEntry> _entries = new();
    private readonly TunnelDefinitionStore _store;
    private readonly TunnelHostOptions _options;
    private readonly ListenPortAllocator _listenPortAllocator;
    private readonly ILogger<TunnelHostRegistry> _logger;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private bool _initialized;
    private bool _shutdown;

    public TunnelHostRegistry(
        TunnelDefinitionStore store,
        IOptions<TunnelHostOptions> options,
        ILogger<TunnelHostRegistry> logger
    )
    {
        _store = store;
        _options = options.Value;
        _listenPortAllocator = new ListenPortAllocator(_options);
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        var state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var document in state.Instances.OrderBy(static item => item.CreatedAt))
        {
            var entry = new TunnelRuntimeEntry(document);
            entry.Status = TunnelStatuses.Stopped;
            _entries[entry.Id] = entry;
        }

        foreach (var entry in _entries.Values.Where(static item => item.Document.DesiredRunning))
        {
            try
            {
                await StartEntryAsync(entry, persistState: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "恢复隧道实例失败: {InstanceId}", entry.Id);
            }
        }

        _initialized = true;
    }

    public TunnelHostStatusResponse GetHostStatus(TunnelHostOptions options)
    {
        return new TunnelHostStatusResponse(
            _options.NodeId,
            options.ManagementUrl,
            _entries.Count,
            _entries.Values.Count(static item => item.Tunnel is not null),
            _startedAt
        );
    }

    public IReadOnlyList<TunnelInstanceResponse> List()
    {
        return _entries
            .Values.OrderByDescending(static item => item.Document.CreatedAt)
            .Select(entry => entry.ToResponse(_options.NodeId))
            .ToArray();
    }

    public TunnelInstanceResponse? Get(Guid id)
    {
        return _entries.TryGetValue(id, out var entry) ? entry.ToResponse(_options.NodeId) : null;
    }

    public async Task<TunnelInstanceResponse> CreateDirectAsync(
        CreateDirectTunnelRequest request,
        CancellationToken cancellationToken
    )
    {
        var document = new TunnelInstanceDocument
        {
            Id = Guid.NewGuid(),
            Kind = TunnelKinds.Direct,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            BatchId = string.IsNullOrWhiteSpace(request.BatchId) ? null : request.BatchId.Trim(),
            DownstreamProtocol = ResolveDownstreamProtocol(request.DownstreamProtocol),
            ListenAddress = ResolveListenAddress(request.ListenAddress),
            PublicHost = ResolvePublicHost(request.PublicHost),
            RequestedListenPort = request.ListenPort ?? 0,
            DesiredRunning = request.AutoStart,
            RemoteProxy = ProxyEndpoint.Parse(request.RemoteProxy).ProxyUri,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var entry = new TunnelRuntimeEntry(document);
        if (!_entries.TryAdd(document.Id, entry))
        {
            throw new InvalidOperationException("创建代理记录失败，请重试。");
        }

        try
        {
            if (request.AutoStart)
            {
                await StartEntryAsync(entry, persistState: false, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }

        return entry.ToResponse(_options.NodeId);
    }

    public async Task<TunnelInstanceResponse> CreatePoolAsync(
        CreatePoolTunnelRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.Upstreams is null || request.Upstreams.Count == 0)
        {
            throw new ArgumentException("上游池不能为空。", nameof(request));
        }

        var upstreams = request
            .Upstreams.Select(static upstream => ProxyEndpoint.Parse(upstream).ProxyUri)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var document = new TunnelInstanceDocument
        {
            Id = Guid.NewGuid(),
            Kind = TunnelKinds.Pool,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            PoolId = ResolvePoolId(request.PoolId),
            DownstreamProtocol = ResolveDownstreamProtocol(request.DownstreamProtocol),
            ListenAddress = ResolveListenAddress(request.ListenAddress),
            PublicHost = ResolvePublicHost(request.PublicHost),
            RequestedListenPort = request.ListenPort ?? 0,
            DesiredRunning = request.AutoStart,
            SelectionPolicy = ResolveSelectionPolicy(request.SelectionPolicy),
            StickyMinutes = ResolveStickyMinutes(request.StickyMinutes),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        document.Upstreams.AddRange(upstreams);

        var entry = new TunnelRuntimeEntry(document);
        if (!_entries.TryAdd(document.Id, entry))
        {
            throw new InvalidOperationException("创建上游池代理记录失败，请重试。");
        }

        try
        {
            if (request.AutoStart)
            {
                await StartEntryAsync(entry, persistState: false, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }

        return entry.ToResponse(_options.NodeId);
    }

    public async Task<TunnelInstanceResponse?> StartAsync(
        Guid id,
        StartTunnelRequest? request,
        CancellationToken cancellationToken
    )
    {
        if (!_entries.TryGetValue(id, out var entry))
        {
            return null;
        }

        try
        {
            ApplyStartRequest(entry, request);
            await StartEntryAsync(entry, persistState: false, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }

        return entry.ToResponse(_options.NodeId);
    }

    public async Task<TunnelInstanceResponse?> StopAsync(Guid id)
    {
        if (!_entries.TryGetValue(id, out var entry))
        {
            return null;
        }

        try
        {
            await StopEntryAsync(entry, preserveDesiredState: false).ConfigureAwait(false);
        }
        finally
        {
            await PersistAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return entry.ToResponse(_options.NodeId);
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        if (!_entries.TryRemove(id, out var entry))
        {
            return false;
        }

        await StopEntryAsync(entry, preserveDesiredState: true).ConfigureAwait(false);
        await PersistAsync(CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    public async Task ShutdownAsync()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        foreach (var entry in _entries.Values)
        {
            await StopEntryAsync(entry, preserveDesiredState: true).ConfigureAwait(false);
        }
    }

    private async Task StartEntryAsync(
        TunnelRuntimeEntry entry,
        bool persistState,
        CancellationToken cancellationToken
    )
    {
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            entry.Document.DesiredRunning = true;
            if (entry.Tunnel is not null)
            {
                return;
            }

            var listenAddress = IPAddress.Parse(entry.Document.ListenAddress);
            var listenPort = ResolveListenPort(entry);
            entry.Router = null;
            entry.Tunnel = await CreateTunnelAsync(
                    entry,
                    listenAddress,
                    listenPort,
                    cancellationToken
                )
                .ConfigureAwait(false);
            entry.Document.LastActiveListenPort = entry.Tunnel.LocalPort;
            entry.Document.StartedAt = DateTimeOffset.UtcNow;
            entry.Document.StoppedAt = null;
            entry.Document.LastError = null;
            entry.Status = TunnelStatuses.Running;
        }
        catch (Exception ex)
        {
            entry.Tunnel = null;
            entry.Status = TunnelStatuses.Error;
            entry.Document.LastError = ex.Message;
            throw;
        }
        finally
        {
            entry.Gate.Release();
        }

        if (persistState)
        {
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StopEntryAsync(TunnelRuntimeEntry entry, bool preserveDesiredState)
    {
        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!preserveDesiredState)
            {
                entry.Document.DesiredRunning = false;
            }

            if (entry.Tunnel is not null)
            {
                await entry.Tunnel.DisposeAsync().ConfigureAwait(false);
                entry.Tunnel = null;
            }

            entry.Router = null;
            entry.Document.StoppedAt = DateTimeOffset.UtcNow;
            if (entry.Status != TunnelStatuses.Error)
            {
                entry.Status = TunnelStatuses.Stopped;
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private Task<IProxyTunnel> CreateTunnelAsync(
        TunnelRuntimeEntry entry,
        IPAddress listenAddress,
        int listenPort,
        CancellationToken cancellationToken
    )
    {
        return entry.Document.Kind switch
        {
            TunnelKinds.Direct => CreateDirectTunnelAsync(
                entry,
                listenAddress,
                listenPort,
                cancellationToken
            ),
            TunnelKinds.Pool => CreatePoolTunnelAsync(
                entry,
                listenAddress,
                listenPort,
                cancellationToken
            ),
            _ => throw new InvalidOperationException($"未知的隧道类型: {entry.Document.Kind}"),
        };
    }

    private Task<IProxyTunnel> CreateDirectTunnelAsync(
        TunnelRuntimeEntry entry,
        IPAddress listenAddress,
        int listenPort,
        CancellationToken cancellationToken
    )
    {
        var remoteProxy = ProxyEndpoint.Parse(entry.Document.RemoteProxy!);
        return entry.Document.DownstreamProtocol switch
        {
            TunnelProtocols.Http => CreateHttpTunnelAsync(
                remoteProxy,
                listenAddress,
                listenPort,
                entry.Document.PublicHost,
                cancellationToken
            ),
            TunnelProtocols.Socks5 => CreateSocks5TunnelAsync(
                remoteProxy,
                listenAddress,
                listenPort,
                entry.Document.PublicHost,
                cancellationToken
            ),
            _ => throw new InvalidOperationException(
                $"未知的下游出口协议: {entry.Document.DownstreamProtocol}"
            ),
        };
    }

    private Task<IProxyTunnel> CreatePoolTunnelAsync(
        TunnelRuntimeEntry entry,
        IPAddress listenAddress,
        int listenPort,
        CancellationToken cancellationToken
    )
    {
        var router = new PersistentUpstreamRouter(
            entry.Document.Upstreams,
            entry.Document.SelectionPolicy,
            entry.Document.StickyMinutes ?? _options.DefaultStickyMinutes,
            _options.FailureCooldownSeconds
        );
        entry.Router = router;

        return entry.Document.DownstreamProtocol switch
        {
            TunnelProtocols.Http => CreateDynamicHttpTunnelAsync(
                router,
                listenAddress,
                listenPort,
                entry.Document.PublicHost,
                cancellationToken
            ),
            TunnelProtocols.Socks5 => CreateDynamicSocks5TunnelAsync(
                router,
                listenAddress,
                listenPort,
                entry.Document.PublicHost,
                cancellationToken
            ),
            _ => throw new InvalidOperationException(
                $"未知的下游出口协议: {entry.Document.DownstreamProtocol}"
            ),
        };
    }

    private static async Task<IProxyTunnel> CreateHttpTunnelAsync(
        ProxyEndpoint remoteProxy,
        IPAddress listenAddress,
        int listenPort,
        string publicHost,
        CancellationToken cancellationToken
    )
    {
        return await HttpProxyTunnel
            .StartAsync(remoteProxy, listenAddress, listenPort, publicHost, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IProxyTunnel> CreateSocks5TunnelAsync(
        ProxyEndpoint remoteProxy,
        IPAddress listenAddress,
        int listenPort,
        string publicHost,
        CancellationToken cancellationToken
    )
    {
        return await Socks5ProxyTunnel
            .StartAsync(remoteProxy, listenAddress, listenPort, publicHost, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IProxyTunnel> CreateDynamicHttpTunnelAsync(
        PersistentUpstreamRouter router,
        IPAddress listenAddress,
        int listenPort,
        string publicHost,
        CancellationToken cancellationToken
    )
    {
        return await DynamicHttpProxyTunnel
            .StartAsync(router, listenAddress, listenPort, publicHost, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IProxyTunnel> CreateDynamicSocks5TunnelAsync(
        PersistentUpstreamRouter router,
        IPAddress listenAddress,
        int listenPort,
        string publicHost,
        CancellationToken cancellationToken
    )
    {
        return await DynamicSocks5ProxyTunnel
            .StartAsync(router, listenAddress, listenPort, publicHost, cancellationToken)
            .ConfigureAwait(false);
    }

    private int ResolveListenPort(TunnelRuntimeEntry entry)
    {
        var occupiedPorts = _entries
            .Values.Where(candidate => candidate.Id != entry.Id && candidate.Tunnel is not null)
            .Select(candidate => candidate.Tunnel!.LocalPort)
            .ToArray();

        var preferredPort = entry.Document.RequestedListenPort;
        if (preferredPort <= 0 && entry.Document.LastActiveListenPort > 0)
        {
            preferredPort = entry.Document.LastActiveListenPort;
        }

        return _listenPortAllocator.ResolvePort(
            entry.Document.ListenAddress,
            preferredPort,
            occupiedPorts
        );
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        var state = new TunnelHostState
        {
            Instances = _entries
                .Values.OrderBy(static item => item.Document.CreatedAt)
                .Select(static item => item.Document)
                .ToList(),
        };
        await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
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

    private static string ResolvePoolId(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new FormatException("上游池 ID 不能为空。");
        }

        return candidate.Trim();
    }

    private static string ResolveDownstreamProtocol(string? candidate)
    {
        var value = string.IsNullOrWhiteSpace(candidate)
            ? TunnelProtocols.Http
            : candidate.Trim().ToLowerInvariant();

        return value switch
        {
            TunnelProtocols.Http => value,
            TunnelProtocols.Socks5 => value,
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

    private static string ResolveSelectionPolicy(string? candidate)
    {
        var value = string.IsNullOrWhiteSpace(candidate)
            ? TunnelSelectionPolicies.Sticky
            : candidate.Trim().ToLowerInvariant();

        return value switch
        {
            TunnelSelectionPolicies.Sticky => value,
            TunnelSelectionPolicies.RoundRobin => value,
            TunnelSelectionPolicies.LeastFailures => value,
            _ => throw new FormatException($"不支持的上游选择策略: {candidate}"),
        };
    }

    private void ApplyStartRequest(TunnelRuntimeEntry entry, StartTunnelRequest? request)
    {
        if (request is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(request.DownstreamProtocol))
        {
            entry.Document.DownstreamProtocol = ResolveDownstreamProtocol(
                request.DownstreamProtocol
            );
        }

        if (!string.IsNullOrWhiteSpace(request.ListenAddress))
        {
            entry.Document.ListenAddress = ResolveListenAddress(request.ListenAddress);
        }

        if (!string.IsNullOrWhiteSpace(request.PublicHost))
        {
            entry.Document.PublicHost = ResolvePublicHost(request.PublicHost);
        }

        if (request.ListenPort.HasValue)
        {
            entry.Document.RequestedListenPort = request.ListenPort.Value;
        }

        if (entry.Document.Kind != TunnelKinds.Pool)
        {
            return;
        }

        if (request.Upstreams is { Count: > 0 })
        {
            var upstreams = request
                .Upstreams.Select(static upstream => ProxyEndpoint.Parse(upstream).ProxyUri)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            entry.Document.Upstreams.Clear();
            entry.Document.Upstreams.AddRange(upstreams);
        }

        if (!string.IsNullOrWhiteSpace(request.SelectionPolicy))
        {
            entry.Document.SelectionPolicy = ResolveSelectionPolicy(request.SelectionPolicy);
        }

        if (request.StickyMinutes.HasValue)
        {
            entry.Document.StickyMinutes = ResolveStickyMinutes(request.StickyMinutes);
        }
    }

    private sealed class TunnelRuntimeEntry
    {
        public TunnelRuntimeEntry(TunnelInstanceDocument document)
        {
            Document = document;
        }

        public Guid Id => Document.Id;

        public TunnelInstanceDocument Document { get; }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public IProxyTunnel? Tunnel { get; set; }

        public PersistentUpstreamRouter? Router { get; set; }

        public string Status { get; set; } = TunnelStatuses.Stopped;

        public TunnelInstanceResponse ToResponse(string nodeId)
        {
            var remoteProxyDisplay = string.IsNullOrWhiteSpace(Document.RemoteProxy)
                ? null
                : ProxyEndpoint.Parse(Document.RemoteProxy).SafeDisplayUri;
            var activePort = Tunnel?.LocalPort ?? 0;
            var healthyUpstreamCount = Router?.HealthyCount ?? Document.Upstreams.Count;

            return new TunnelInstanceResponse(
                Document.Id,
                nodeId,
                Document.Kind,
                Document.Note,
                Document.BatchId,
                Document.PoolId,
                Document.DownstreamProtocol,
                Document.ListenAddress,
                Document.PublicHost,
                Document.RequestedListenPort,
                activePort,
                Document.DesiredRunning,
                Document.RemoteProxy,
                remoteProxyDisplay,
                Document.Upstreams.ToArray(),
                healthyUpstreamCount,
                Document.SelectionPolicy,
                Document.StickyMinutes,
                Tunnel?.LocalProxyUri,
                Router?.LastSelectedUpstream,
                Router?.LastSelectedUpstreamDisplay,
                Status,
                Document.CreatedAt,
                Document.StartedAt,
                Document.StoppedAt,
                Document.LastError
            );
        }
    }
}
