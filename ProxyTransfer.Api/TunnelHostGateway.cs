namespace ProxyTransfer.Api;

internal sealed class TunnelHostGateway
{
    private readonly TunnelHostApiClient _client;
    private readonly UpstreamPoolRegistry _upstreamPools;

    public TunnelHostGateway(TunnelHostApiClient client, UpstreamPoolRegistry upstreamPools)
    {
        _client = client;
        _upstreamPools = upstreamPools;
    }

    public async Task<IReadOnlyList<ProxyTunnelResponse>> ListDirectAsync(
        CancellationToken cancellationToken
    )
    {
        var instances = await _client.ListAsync(cancellationToken).ConfigureAwait(false);
        return instances
            .Where(static item => item.Kind == "direct")
            .OrderByDescending(static item => item.CreatedAt)
            .Select(MapDirect)
            .ToArray();
    }

    public async Task<IReadOnlyList<BatchSummaryResponse>> ListDirectBatchesAsync(
        CancellationToken cancellationToken
    )
    {
        var tunnels = await ListDirectAsync(cancellationToken).ConfigureAwait(false);
        return tunnels
            .Where(static item => !string.IsNullOrWhiteSpace(item.BatchId))
            .GroupBy(static item => item.BatchId!)
            .Select(group => new BatchSummaryResponse(
                group.Key,
                group.Count(),
                group.Count(static item => item.Status == "Running")
            ))
            .OrderByDescending(static item => item.BatchId)
            .ToArray();
    }

    public async Task<ImportProxiesResponse> ImportDirectAsync(
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
            int? listenPort = request.FirstListenPort switch
            {
                -1 => -1,
                int firstListenPort => firstListenPort + index,
                null => null,
            };

            var created = await AddDirectAsync(
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

    public async Task<ProxyTunnelResponse> AddDirectAsync(
        AddProxyRequest request,
        CancellationToken cancellationToken
    )
    {
        var created = await _client
            .CreateDirectAsync(
                new TunnelHostCreateDirectRequest(
                    request.Proxy,
                    request.DownstreamProtocol,
                    request.Note,
                    request.BatchId,
                    request.ListenAddress,
                    request.PublicHost,
                    request.ListenPort,
                    request.AutoStart
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        return MapDirect(created);
    }

    public async Task<ProxyTunnelResponse?> GetDirectAsync(
        Guid id,
        CancellationToken cancellationToken
    )
    {
        var instance = await _client.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return instance is null || instance.Kind != "direct" ? null : MapDirect(instance);
    }

    public async Task<ProxyTunnelResponse?> StartDirectAsync(
        Guid id,
        StartProxyRequest? request,
        CancellationToken cancellationToken
    )
    {
        var started = await _client
            .StartAsync(
                id,
                request is null
                    ? null
                    : new TunnelHostStartRequest(
                        request.DownstreamProtocol,
                        request.ListenAddress,
                        request.PublicHost,
                        request.ListenPort,
                        null,
                        null,
                        null
                    ),
                cancellationToken
            )
            .ConfigureAwait(false);
        return started is null || started.Kind != "direct" ? null : MapDirect(started);
    }

    public async Task<ProxyTunnelResponse?> StopDirectAsync(
        Guid id,
        CancellationToken cancellationToken
    )
    {
        var stopped = await _client.StopAsync(id, cancellationToken).ConfigureAwait(false);
        return stopped is null || stopped.Kind != "direct" ? null : MapDirect(stopped);
    }

    public async Task<int> StopBatchAsync(string batchId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(batchId))
        {
            throw new ArgumentException("批次号不能为空。", nameof(batchId));
        }

        var targets = await ListDirectAsync(cancellationToken).ConfigureAwait(false);
        var batchTargets = targets
            .Where(item => string.Equals(item.BatchId, batchId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var target in batchTargets)
        {
            await StopDirectAsync(target.Id, cancellationToken).ConfigureAwait(false);
        }

        return batchTargets.Length;
    }

    public async Task<IReadOnlyList<FixedProxyResponse>> ListFixedAsync(
        CancellationToken cancellationToken
    )
    {
        var instances = await _client.ListAsync(cancellationToken).ConfigureAwait(false);
        return instances
            .Where(static item => item.Kind == "pool")
            .OrderByDescending(static item => item.CreatedAt)
            .Select(MapFixed)
            .ToArray();
    }

    public async Task<FixedProxyResponse?> GetFixedAsync(
        Guid id,
        CancellationToken cancellationToken
    )
    {
        var instance = await _client.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return instance is null || instance.Kind != "pool" ? null : MapFixed(instance);
    }

    public async Task<FixedProxyResponse> AddFixedAsync(
        FixedProxyRequest request,
        CancellationToken cancellationToken
    )
    {
        var pool = _upstreamPools.GetPool(request.PoolId);
        var created = await _client
            .CreatePoolAsync(
                new TunnelHostCreatePoolRequest(
                    pool.PoolId,
                    pool.Items.Select(static item => item.Proxy).ToArray(),
                    request.DownstreamProtocol,
                    request.Note,
                    request.ListenAddress,
                    request.PublicHost,
                    request.ListenPort,
                    request.SelectionPolicy,
                    request.StickyMinutes,
                    request.AutoStart
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        return MapFixed(created);
    }

    public async Task<FixedProxyResponse?> StartFixedAsync(
        Guid id,
        CancellationToken cancellationToken
    )
    {
        var current = await GetFixedAsync(id, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return null;
        }

        var pool = _upstreamPools.GetPool(current.PoolId);
        var started = await _client
            .StartAsync(
                id,
                new TunnelHostStartRequest(
                    current.DownstreamProtocol,
                    current.ListenAddress,
                    current.PublicHost,
                    current.RequestedListenPort,
                    pool.Items.Select(static item => item.Proxy).ToArray(),
                    current.SelectionPolicy,
                    current.StickyMinutes
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        return started is null || started.Kind != "pool" ? null : MapFixed(started);
    }

    public async Task<FixedProxyResponse?> StopFixedAsync(
        Guid id,
        CancellationToken cancellationToken
    )
    {
        var stopped = await _client.StopAsync(id, cancellationToken).ConfigureAwait(false);
        return stopped is null || stopped.Kind != "pool" ? null : MapFixed(stopped);
    }

    public async Task<bool> DeleteFixedAsync(Guid id, CancellationToken cancellationToken)
    {
        // 先停止再删除，确保下游连接被关闭
        await StopFixedAsync(id, cancellationToken).ConfigureAwait(false);
        return await _client.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public Task<TunnelHostPortRangeResponse> GetPortRangeAsync(CancellationToken cancellationToken)
    {
        return _client.GetPortRangeAsync(cancellationToken);
    }

    private ProxyTunnelResponse MapDirect(TunnelHostInstanceResponse instance)
    {
        return new ProxyTunnelResponse(
            instance.Id,
            instance.BatchId,
            instance.Note,
            instance.RemoteProxy ?? string.Empty,
            instance.RemoteProxyDisplay ?? instance.RemoteProxy ?? string.Empty,
            instance.DownstreamProtocol,
            instance.ListenAddress,
            instance.PublicHost,
            instance.RequestedListenPort,
            instance.ActiveListenPort,
            instance.ForwardedProxy,
            instance.Status,
            instance.CreatedAt,
            instance.StartedAt,
            instance.StoppedAt,
            instance.LastError
        );
    }

    private FixedProxyResponse MapFixed(TunnelHostInstanceResponse instance)
    {
        UpstreamPoolSnapshot? poolSnapshot = null;
        if (!string.IsNullOrWhiteSpace(instance.PoolId))
        {
            try
            {
                poolSnapshot = _upstreamPools.GetPoolSnapshot(instance.PoolId);
            }
            catch (InvalidOperationException) { }
        }

        var totalUpstreamCount = poolSnapshot?.TotalCount ?? instance.Upstreams.Count;
        var healthyUpstreamCount = poolSnapshot?.HealthyCount ?? instance.HealthyUpstreamCount;

        return new FixedProxyResponse(
            instance.Id,
            instance.PoolId ?? string.Empty,
            instance.Note,
            instance.DownstreamProtocol,
            instance.ListenAddress,
            instance.PublicHost,
            instance.RequestedListenPort,
            instance.ActiveListenPort,
            instance.ForwardedProxy,
            instance.SelectionPolicy ?? "sticky",
            instance.StickyMinutes ?? 0,
            totalUpstreamCount,
            healthyUpstreamCount,
            instance.LastSelectedUpstream,
            instance.LastSelectedUpstreamDisplay,
            instance.Status,
            instance.CreatedAt,
            instance.StartedAt,
            instance.StoppedAt,
            instance.LastError
        );
    }
}
