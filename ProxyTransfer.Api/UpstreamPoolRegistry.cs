using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProxyTransfer.Tunnel;

namespace ProxyTransfer.Api;

public sealed class UpstreamPoolRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly ConcurrentDictionary<string, UpstreamPoolEntry> _pools = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly ProxyTunnelHostOptions _options;
    private readonly string _stateFilePath;
    private readonly ILogger<UpstreamPoolRegistry> _logger;

    public UpstreamPoolRegistry(
        IOptions<ProxyTunnelHostOptions> options,
        IHostEnvironment environment,
        ILogger<UpstreamPoolRegistry> logger
    )
    {
        _options = options.Value;
        _stateFilePath = ResolveStateFilePath(
            _options.UpstreamPoolStateFilePath,
            environment.ContentRootPath
        );
        _logger = logger;
        LoadState();
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

        PersistState();

        return new ImportUpstreamPoolResponse(
            pool.PoolId,
            importedCount,
            pool.Items.Count,
            pool.Items.Select(item => item.ToResponse(pool.PoolId)).ToArray()
        );
    }

    public ImportUpstreamPoolResponse Append(string poolId, UpdateUpstreamPoolRequest request)
    {
        if (string.IsNullOrWhiteSpace(poolId))
        {
            throw new ArgumentException("上游池 ID 不能为空。", nameof(poolId));
        }

        return Import(
            new ImportUpstreamPoolRequest(request.ProxyText, poolId.Trim(), request.Note)
        );
    }

    public DeleteUpstreamPoolProxiesResponse DeleteProxies(
        string poolId,
        DeleteUpstreamPoolProxiesRequest request
    )
    {
        var pool = GetPoolEntry(poolId);
        var targetUris = ParseProxyUris(request.ProxyText);
        var targetIds = request.UpstreamIds?.Where(static id => id != Guid.Empty).ToHashSet() ?? [];

        if (!request.RemoveFailed && targetIds.Count == 0 && targetUris.Count == 0)
        {
            throw new InvalidOperationException("请至少选择一种删除方式。");
        }

        int removedCount;

        lock (pool.SyncRoot)
        {
            removedCount = pool.Items.RemoveAll(item =>
                ShouldRemove(item, request.RemoveFailed, targetIds, targetUris)
            );
            NormalizeNextIndex(pool);
            pool.Touch();
        }

        PersistState();

        return new DeleteUpstreamPoolProxiesResponse(
            pool.PoolId,
            removedCount,
            pool.Items.Count,
            pool.Items.Select(item => item.ToResponse(pool.PoolId)).ToArray()
        );
    }

    public UpstreamLease Acquire(string poolId)
    {
        var pool = GetPoolEntry(poolId);
        lock (pool.SyncRoot)
        {
            var candidates = GetHealthyCandidates(pool);
            if (candidates.Length == 0)
            {
                throw new InvalidOperationException($"上游池 {poolId} 当前没有可用代理。");
            }

            candidates = candidates
                .OrderBy(static item => item.FailureCount)
                .ThenBy(static item => item.LastSuccessAt ?? item.CreatedAt)
                .ToArray();

            pool.NextIndex = (pool.NextIndex + 1) % candidates.Length;
            var selected = candidates[pool.NextIndex];
            return new UpstreamLease(selected.Id, selected.Endpoint);
        }
    }

    public UpstreamLease AcquireRoundRobin(string poolId)
    {
        var pool = GetPoolEntry(poolId);
        lock (pool.SyncRoot)
        {
            var candidates = GetHealthyCandidates(pool);
            if (candidates.Length == 0)
            {
                throw new InvalidOperationException($"上游池 {poolId} 当前没有可用代理。");
            }

            pool.NextIndex = (pool.NextIndex + 1) % candidates.Length;
            var selected = candidates[pool.NextIndex];
            return new UpstreamLease(selected.Id, selected.Endpoint);
        }
    }

    public UpstreamLease AcquireLeastFailures(string poolId)
    {
        var pool = GetPoolEntry(poolId);
        lock (pool.SyncRoot)
        {
            var candidates = GetHealthyCandidates(pool)
                .OrderBy(static item => item.FailureCount)
                .ThenByDescending(static item => item.LastSuccessAt ?? item.CreatedAt)
                .ThenBy(static item => item.Id)
                .ToArray();

            if (candidates.Length == 0)
            {
                throw new InvalidOperationException($"上游池 {poolId} 当前没有可用代理。");
            }

            var selected = candidates[0];
            return new UpstreamLease(selected.Id, selected.Endpoint);
        }
    }

    public void MarkSuccess(string poolId, Guid upstreamId)
    {
        var item = TryGetPoolItem(poolId, upstreamId);
        if (item is null)
        {
            return;
        }

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
        var item = TryGetPoolItem(poolId, upstreamId);
        if (item is null)
        {
            return;
        }

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
        var item = TryGetPoolItem(poolId, upstreamId);
        if (item is null)
        {
            return false;
        }

        lock (item.SyncRoot)
        {
            return item.DisabledUntil is null || item.DisabledUntil <= DateTimeOffset.UtcNow;
        }
    }

    public ProxyEndpoint GetEndpoint(string poolId, Guid upstreamId)
    {
        var item = GetPoolItem(poolId, upstreamId);
        return item.Endpoint;
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

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
            {
                return;
            }

            var json = File.ReadAllText(_stateFilePath);
            var state = JsonSerializer.Deserialize<UpstreamPoolState>(json, JsonOptions);
            if (state?.Pools is null || state.Pools.Count == 0)
            {
                return;
            }

            foreach (var poolDocument in state.Pools)
            {
                if (string.IsNullOrWhiteSpace(poolDocument.PoolId))
                {
                    continue;
                }

                _pools[poolDocument.PoolId] = UpstreamPoolEntry.FromDocument(poolDocument);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载上游池状态文件失败: {Path}", _stateFilePath);
        }
    }

    private void PersistState()
    {
        try
        {
            var directory = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var snapshot = new UpstreamPoolState
            {
                Pools = _pools
                    .Values.OrderByDescending(static pool => pool.UpdatedAt)
                    .Select(static pool => pool.ToDocument())
                    .ToList(),
            };

            var tempFilePath = _stateFilePath + ".tmp";
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(tempFilePath, json);
            File.Move(tempFilePath, _stateFilePath, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存上游池状态文件失败: {Path}", _stateFilePath);
        }
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

    private UpstreamProxyEntry? TryGetPoolItem(string poolId, Guid upstreamId)
    {
        if (string.IsNullOrWhiteSpace(poolId))
        {
            return null;
        }

        if (!_pools.TryGetValue(poolId.Trim(), out var pool))
        {
            return null;
        }

        lock (pool.SyncRoot)
        {
            return pool.Items.FirstOrDefault(item => item.Id == upstreamId);
        }
    }

    private static UpstreamProxyEntry[] GetHealthyCandidates(UpstreamPoolEntry pool)
    {
        var now = DateTimeOffset.UtcNow;
        return pool
            .Items.Where(item => item.DisabledUntil is null || item.DisabledUntil <= now)
            .ToArray();
    }

    private static void NormalizeNextIndex(UpstreamPoolEntry pool)
    {
        if (pool.Items.Count == 0)
        {
            pool.NextIndex = -1;
            return;
        }

        if (pool.NextIndex >= pool.Items.Count)
        {
            pool.NextIndex %= pool.Items.Count;
        }
    }

    private static HashSet<string> ParseProxyUris(string? proxyText)
    {
        if (string.IsNullOrWhiteSpace(proxyText))
        {
            return [];
        }

        return proxyText
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .Select(ProxyEndpoint.Parse)
            .Select(static endpoint => endpoint.ProxyUri)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool ShouldRemove(
        UpstreamProxyEntry item,
        bool removeFailed,
        HashSet<Guid> targetIds,
        HashSet<string> targetUris
    )
    {
        if (targetIds.Contains(item.Id))
        {
            return true;
        }

        if (targetUris.Contains(item.Endpoint.ProxyUri))
        {
            return true;
        }

        if (!removeFailed)
        {
            return false;
        }

        lock (item.SyncRoot)
        {
            return item.Status == UpstreamProxyStatus.Unhealthy
                || item.DisabledUntil is not null
                || !string.IsNullOrWhiteSpace(item.LastError)
                || item.FailureCount > 0;
        }
    }

    private static string ResolveStateFilePath(string configuredPath, string contentRootPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? "App_Data/upstream-pools.json"
            : configuredPath.Trim();

        return Path.IsPathRooted(path) ? path : Path.Combine(contentRootPath, path);
    }

    private sealed class UpstreamPoolEntry
    {
        public UpstreamPoolEntry(
            string poolId,
            string? note,
            DateTimeOffset? createdAt = null,
            DateTimeOffset? updatedAt = null,
            int nextIndex = -1
        )
        {
            PoolId = poolId;
            Note = note;
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
            UpdatedAt = updatedAt ?? CreatedAt;
            NextIndex = nextIndex;
        }

        public string PoolId { get; }

        public string? Note { get; set; }

        public List<UpstreamProxyEntry> Items { get; } = [];

        public object SyncRoot { get; } = new();

        public int NextIndex { get; set; }

        public DateTimeOffset CreatedAt { get; }

        public DateTimeOffset UpdatedAt { get; private set; }

        public static UpstreamPoolEntry FromDocument(UpstreamPoolStateItemDocument document)
        {
            var pool = new UpstreamPoolEntry(
                document.PoolId,
                document.Note,
                document.CreatedAt,
                document.UpdatedAt,
                document.NextIndex
            );

            foreach (var itemDocument in document.Items)
            {
                if (string.IsNullOrWhiteSpace(itemDocument.Proxy))
                {
                    continue;
                }

                pool.Items.Add(UpstreamProxyEntry.FromDocument(itemDocument));
            }

            NormalizeNextIndex(pool);
            return pool;
        }

        public void Touch()
        {
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        public UpstreamPoolStateItemDocument ToDocument()
        {
            return new UpstreamPoolStateItemDocument(
                PoolId,
                Note,
                NextIndex,
                CreatedAt,
                UpdatedAt,
                Items.Select(static item => item.ToDocument()).ToArray()
            );
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
        public UpstreamProxyEntry(
            Guid id,
            ProxyEndpoint endpoint,
            UpstreamProxyStatus status = UpstreamProxyStatus.Unknown,
            int failureCount = 0,
            DateTimeOffset? createdAt = null,
            DateTimeOffset? lastCheckedAt = null,
            DateTimeOffset? lastSuccessAt = null,
            DateTimeOffset? lastFailureAt = null,
            DateTimeOffset? disabledUntil = null,
            string? lastError = null
        )
        {
            Id = id;
            Endpoint = endpoint;
            Status = status;
            FailureCount = failureCount;
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
            LastCheckedAt = lastCheckedAt;
            LastSuccessAt = lastSuccessAt;
            LastFailureAt = lastFailureAt;
            DisabledUntil = disabledUntil;
            LastError = lastError;
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

        public static UpstreamProxyEntry FromDocument(UpstreamProxyStateItemDocument document)
        {
            return new UpstreamProxyEntry(
                document.Id,
                ProxyEndpoint.Parse(document.Proxy),
                ParseStatus(document.Status),
                document.FailureCount,
                document.CreatedAt,
                document.LastCheckedAt,
                document.LastSuccessAt,
                document.LastFailureAt,
                document.DisabledUntil,
                document.LastError
            );
        }

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

        public UpstreamProxyStateItemDocument ToDocument()
        {
            return new UpstreamProxyStateItemDocument(
                Id,
                Endpoint.ProxyUri,
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

    private static UpstreamProxyStatus ParseStatus(string? value)
    {
        return Enum.TryParse<UpstreamProxyStatus>(value, ignoreCase: true, out var status)
            ? status
            : UpstreamProxyStatus.Unknown;
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
