using System.Collections.Concurrent;
using ProxyTransfer.Tunnel;

namespace ProxyTransfer.Api;

public sealed class UpstreamPoolRegistry
{
    private readonly ConcurrentDictionary<string, UpstreamPoolEntry> _pools = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly ProxyTunnelHostOptions _options;

    public UpstreamPoolRegistry(ProxyTunnelHostOptions options)
    {
        _options = options;
    }

    public IReadOnlyList<UpstreamPoolResponse> ListPools()
    {
        return _pools
            .Values.OrderByDescending(static pool => pool.UpdatedAt)
            .Select(static pool => pool.ToResponse())
            .ToArray();
    }

    public UpstreamPoolDetailsResponse GetPool(string poolId)
    {
        return GetPoolEntry(poolId).ToDetailsResponse();
    }

    public UpstreamPoolSnapshot GetPoolSnapshot(string poolId)
    {
        return GetPoolEntry(poolId).ToSnapshot();
    }

    public ImportUpstreamPoolResponse Import(ImportUpstreamPoolRequest request)
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
                "没有可导入的上游代理。请按每行一个 HTTP 或 SOCKS5 代理填写。"
            );
        }

        var poolId = string.IsNullOrWhiteSpace(request.PoolId)
            ? $"pool-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}"
            : request.PoolId.Trim();

        var pool = _pools.GetOrAdd(poolId, id => new UpstreamPoolEntry(id, request.Note?.Trim()));

        var importedCount = 0;

        lock (pool.SyncRoot)
        {
            if (!string.IsNullOrWhiteSpace(request.Note))
            {
                pool.Note = request.Note.Trim();
            }

            foreach (var line in lines)
            {
                var endpoint = ProxyEndpoint.Parse(line);
                if (
                    pool.Items.Any(item =>
                        string.Equals(
                            item.Endpoint.ProxyUri,
                            endpoint.ProxyUri,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                )
                {
                    continue;
                }

                pool.Items.Add(new UpstreamProxyEntry(Guid.NewGuid(), endpoint));
                importedCount++;
            }

            pool.Touch();
        }

        return new ImportUpstreamPoolResponse(
            pool.PoolId,
            importedCount,
            pool.Items.Count,
            pool.Items.Select(item => item.ToResponse(pool.PoolId)).ToArray()
        );
    }

    public UpstreamLease Acquire(string poolId)
    {
        var pool = GetPoolEntry(poolId);
        lock (pool.SyncRoot)
        {
            var now = DateTimeOffset.UtcNow;
            var candidates = pool
                .Items.Where(item => item.DisabledUntil is null || item.DisabledUntil <= now)
                .OrderBy(static item => item.FailureCount)
                .ThenBy(static item => item.LastSuccessAt ?? item.CreatedAt)
                .ToArray();

            if (candidates.Length == 0)
            {
                throw new InvalidOperationException($"上游池 {poolId} 当前没有可用代理。");
            }

            pool.NextIndex = (pool.NextIndex + 1) % candidates.Length;
            var selected = candidates[pool.NextIndex];
            return new UpstreamLease(selected.Id, selected.Endpoint);
        }
    }

    public void MarkSuccess(string poolId, Guid upstreamId)
    {
        var item = GetPoolItem(poolId, upstreamId);
        lock (item.SyncRoot)
        {
            item.Status = UpstreamProxyStatus.Healthy;
            item.LastCheckedAt = DateTimeOffset.UtcNow;
            item.LastSuccessAt = item.LastCheckedAt;
            item.LastError = null;
            item.DisabledUntil = null;
            item.FailureCount = 0;
        }

        GetPoolEntry(poolId).Touch();
    }

    public void MarkFailure(string poolId, Guid upstreamId, Exception exception)
    {
        var item = GetPoolItem(poolId, upstreamId);
        lock (item.SyncRoot)
        {
            item.Status = UpstreamProxyStatus.Unhealthy;
            item.LastCheckedAt = DateTimeOffset.UtcNow;
            item.LastFailureAt = item.LastCheckedAt;
            item.LastError = exception.Message;
            item.FailureCount++;
            item.DisabledUntil = item.LastCheckedAt.Value.AddSeconds(
                _options.FailureCooldownSeconds
            );
        }

        GetPoolEntry(poolId).Touch();
    }

    public bool IsLeaseHealthy(string poolId, Guid upstreamId)
    {
        var item = GetPoolItem(poolId, upstreamId);
        lock (item.SyncRoot)
        {
            return item.DisabledUntil is null || item.DisabledUntil <= DateTimeOffset.UtcNow;
        }
    }

    public string? GetSafeDisplay(Guid upstreamId)
    {
        foreach (var pool in _pools.Values)
        {
            lock (pool.SyncRoot)
            {
                var item = pool.Items.FirstOrDefault(candidate => candidate.Id == upstreamId);
                if (item is not null)
                {
                    return item.Endpoint.SafeDisplayUri;
                }
            }
        }

        return null;
    }

    public IReadOnlyList<(string PoolId, Guid UpstreamId, ProxyEndpoint Endpoint)> GetProbeTargets()
    {
        var targets = new List<(string PoolId, Guid UpstreamId, ProxyEndpoint Endpoint)>();
        foreach (var pool in _pools.Values)
        {
            lock (pool.SyncRoot)
            {
                targets.AddRange(pool.Items.Select(item => (pool.PoolId, item.Id, item.Endpoint)));
            }
        }

        return targets;
    }

    private UpstreamPoolEntry GetPoolEntry(string poolId)
    {
        if (string.IsNullOrWhiteSpace(poolId))
        {
            throw new ArgumentException("上游池 ID 不能为空。", nameof(poolId));
        }

        if (!_pools.TryGetValue(poolId.Trim(), out var pool))
        {
            throw new InvalidOperationException($"未找到上游池: {poolId}");
        }

        return pool;
    }

    private UpstreamProxyEntry GetPoolItem(string poolId, Guid upstreamId)
    {
        var pool = GetPoolEntry(poolId);
        lock (pool.SyncRoot)
        {
            return pool.Items.FirstOrDefault(item => item.Id == upstreamId)
                ?? throw new InvalidOperationException($"未找到上游代理: {upstreamId}");
        }
    }

    private sealed class UpstreamPoolEntry
    {
        public UpstreamPoolEntry(string poolId, string? note)
        {
            PoolId = poolId;
            Note = note;
            CreatedAt = DateTimeOffset.UtcNow;
            UpdatedAt = CreatedAt;
        }

        public string PoolId { get; }

        public string? Note { get; set; }

        public List<UpstreamProxyEntry> Items { get; } = [];

        public object SyncRoot { get; } = new();

        public int NextIndex { get; set; } = -1;

        public DateTimeOffset CreatedAt { get; }

        public DateTimeOffset UpdatedAt { get; private set; }

        public void Touch()
        {
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        public UpstreamPoolResponse ToResponse()
        {
            var healthyCount = Items.Count(static item => item.IsHealthy);
            return new UpstreamPoolResponse(
                PoolId,
                Note,
                Items.Count,
                healthyCount,
                CreatedAt,
                UpdatedAt
            );
        }

        public UpstreamPoolDetailsResponse ToDetailsResponse()
        {
            var healthyCount = Items.Count(static item => item.IsHealthy);
            return new UpstreamPoolDetailsResponse(
                PoolId,
                Note,
                Items.Count,
                healthyCount,
                CreatedAt,
                UpdatedAt,
                Items.Select(item => item.ToResponse(PoolId)).ToArray()
            );
        }

        public UpstreamPoolSnapshot ToSnapshot()
        {
            var healthyCount = Items.Count(static item => item.IsHealthy);
            return new UpstreamPoolSnapshot(
                PoolId,
                Note,
                Items.Count,
                healthyCount,
                CreatedAt,
                UpdatedAt
            );
        }
    }

    private sealed class UpstreamProxyEntry
    {
        public UpstreamProxyEntry(Guid id, ProxyEndpoint endpoint)
        {
            Id = id;
            Endpoint = endpoint;
            CreatedAt = DateTimeOffset.UtcNow;
            Status = UpstreamProxyStatus.Unknown;
        }

        public Guid Id { get; }

        public ProxyEndpoint Endpoint { get; }

        public UpstreamProxyStatus Status { get; set; }

        public int FailureCount { get; set; }

        public DateTimeOffset CreatedAt { get; }

        public DateTimeOffset? LastCheckedAt { get; set; }

        public DateTimeOffset? LastSuccessAt { get; set; }

        public DateTimeOffset? LastFailureAt { get; set; }

        public DateTimeOffset? DisabledUntil { get; set; }

        public string? LastError { get; set; }

        public object SyncRoot { get; } = new();

        public bool IsHealthy => DisabledUntil is null || DisabledUntil <= DateTimeOffset.UtcNow;

        public UpstreamProxyResponse ToResponse(string poolId)
        {
            return new UpstreamProxyResponse(
                Id,
                poolId,
                Endpoint.ProxyUri,
                Endpoint.SafeDisplayUri,
                Status.ToString(),
                FailureCount,
                CreatedAt,
                LastCheckedAt,
                LastSuccessAt,
                LastFailureAt,
                DisabledUntil,
                LastError
            );
        }
    }

    private enum UpstreamProxyStatus
    {
        Unknown,
        Healthy,
        Unhealthy,
    }
}

public sealed record UpstreamPoolSnapshot(
    string PoolId,
    string? Note,
    int TotalCount,
    int HealthyCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);
