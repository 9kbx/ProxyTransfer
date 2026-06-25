using System.Collections.Concurrent;
using ProxyTransfer.Tunnel;

namespace ProxyTransfer.TunnelHost;

internal sealed class PersistentUpstreamRouter : IUpstreamRouter
{
    private readonly object _syncRoot = new();
    private readonly List<UpstreamState> _upstreams;
    private readonly ConcurrentDictionary<string, StickySelection> _stickySelections = new();
    private readonly string _selectionPolicy;
    private readonly int _stickyMinutes;
    private readonly int _failureCooldownSeconds;
    private int _nextIndex = -1;

    public PersistentUpstreamRouter(
        IEnumerable<string> upstreams,
        string? selectionPolicy,
        int stickyMinutes,
        int failureCooldownSeconds
    )
    {
        _upstreams = upstreams
            .Select(static upstream => new UpstreamState(
                Guid.NewGuid(),
                ProxyEndpoint.Parse(upstream)
            ))
            .ToList();
        _selectionPolicy = NormalizeSelectionPolicy(selectionPolicy);
        _stickyMinutes = stickyMinutes;
        _failureCooldownSeconds = failureCooldownSeconds;

        if (_upstreams.Count == 0)
        {
            throw new InvalidOperationException("上游池不能为空。至少需要一个可用上游代理。");
        }
    }

    public int HealthyCount
    {
        get
        {
            lock (_syncRoot)
            {
                return GetHealthyCandidates(DateTimeOffset.UtcNow).Count;
            }
        }
    }

    public string? LastSelectedUpstream { get; private set; }

    public string? LastSelectedUpstreamDisplay { get; private set; }

    public ValueTask<UpstreamLease> SelectAsync(
        ProxyConnectRequest request,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            var now = DateTimeOffset.UtcNow;
            var candidates = GetHealthyCandidates(now);
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException("当前没有可用的上游代理。");
            }

            var selected = _selectionPolicy switch
            {
                TunnelSelectionPolicies.Sticky => SelectStickyCandidate(request, candidates, now),
                TunnelSelectionPolicies.RoundRobin => SelectRoundRobinCandidate(candidates),
                TunnelSelectionPolicies.LeastFailures => SelectLeastFailuresCandidate(candidates),
                _ => throw new InvalidOperationException(
                    $"不支持的上游选择策略: {_selectionPolicy}"
                ),
            };

            LastSelectedUpstream = selected.Endpoint.ProxyUri;
            LastSelectedUpstreamDisplay = selected.Endpoint.SafeDisplayUri;
            return ValueTask.FromResult(new UpstreamLease(selected.Id, selected.Endpoint));
        }
    }

    public ValueTask ReportSuccessAsync(UpstreamLease lease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            var upstream = _upstreams.FirstOrDefault(item => item.Id == lease.UpstreamId);
            if (upstream is null)
            {
                return ValueTask.CompletedTask;
            }

            upstream.FailureCount = 0;
            upstream.DisabledUntil = null;
            upstream.LastSuccessAt = DateTimeOffset.UtcNow;
            upstream.LastError = null;
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask ReportFailureAsync(
        UpstreamLease lease,
        Exception exception,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            var upstream = _upstreams.FirstOrDefault(item => item.Id == lease.UpstreamId);
            if (upstream is null)
            {
                return ValueTask.CompletedTask;
            }

            upstream.FailureCount++;
            upstream.LastFailureAt = DateTimeOffset.UtcNow;
            upstream.DisabledUntil = upstream.LastFailureAt.Value.AddSeconds(
                _failureCooldownSeconds
            );
            upstream.LastError = exception.Message;
            return ValueTask.CompletedTask;
        }
    }

    private static string NormalizeSelectionPolicy(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? TunnelSelectionPolicies.Sticky
            : value.Trim().ToLowerInvariant();

        return normalized switch
        {
            TunnelSelectionPolicies.Sticky => normalized,
            TunnelSelectionPolicies.RoundRobin => normalized,
            TunnelSelectionPolicies.LeastFailures => normalized,
            _ => throw new FormatException($"不支持的上游选择策略: {value}"),
        };
    }

    private List<UpstreamState> GetHealthyCandidates(DateTimeOffset now)
    {
        return _upstreams
            .Where(item => item.DisabledUntil is null || item.DisabledUntil <= now)
            .ToList();
    }

    private UpstreamState SelectStickyCandidate(
        ProxyConnectRequest request,
        IReadOnlyList<UpstreamState> candidates,
        DateTimeOffset now
    )
    {
        var key = $"{request.TargetHost}:{request.TargetPort}";
        if (
            _stickySelections.TryGetValue(key, out var sticky)
            && sticky.ExpiresAt > now
            && candidates.Any(candidate => candidate.Id == sticky.UpstreamId)
        )
        {
            return candidates.First(candidate => candidate.Id == sticky.UpstreamId);
        }

        var selected = SelectLeastFailuresCandidate(candidates);
        _stickySelections[key] = new StickySelection(selected.Id, now.AddMinutes(_stickyMinutes));
        return selected;
    }

    private UpstreamState SelectRoundRobinCandidate(IReadOnlyList<UpstreamState> candidates)
    {
        _nextIndex = (_nextIndex + 1) % candidates.Count;
        return candidates[_nextIndex];
    }

    private static UpstreamState SelectLeastFailuresCandidate(
        IReadOnlyList<UpstreamState> candidates
    )
    {
        return candidates
            .OrderBy(static item => item.FailureCount)
            .ThenByDescending(static item => item.LastSuccessAt ?? DateTimeOffset.MinValue)
            .ThenBy(static item => item.Id)
            .First();
    }

    private sealed class UpstreamState
    {
        public UpstreamState(Guid id, ProxyEndpoint endpoint)
        {
            Id = id;
            Endpoint = endpoint;
        }

        public Guid Id { get; }

        public ProxyEndpoint Endpoint { get; }

        public int FailureCount { get; set; }

        public DateTimeOffset? DisabledUntil { get; set; }

        public DateTimeOffset? LastSuccessAt { get; set; }

        public DateTimeOffset? LastFailureAt { get; set; }

        public string? LastError { get; set; }
    }

    private sealed record StickySelection(Guid UpstreamId, DateTimeOffset ExpiresAt);
}
