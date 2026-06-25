using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProxyTransfer.Tunnel;

namespace ProxyTransfer.Api;

public sealed class ProxyTestService
{
    private static readonly Uri ProbeUri = new("https://api.ipify.org/");
    private const int HistoryLimit = 24;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly Lock _historyGate = new();
    private readonly LinkedList<ProxyTestResponse> _history = new();
    private readonly string _historyFilePath;
    private readonly Lock _upstreamPoolHistoryGate = new();
    private readonly LinkedList<UpstreamPoolTestResponse> _upstreamPoolHistory = new();
    private readonly string _upstreamPoolHistoryFilePath;
    private readonly ILogger<ProxyTestService> _logger;

    public ProxyTestService(
        IOptions<ProxyTunnelHostOptions> options,
        IHostEnvironment environment,
        ILogger<ProxyTestService> logger
    )
    {
        _logger = logger;
        _historyFilePath = ResolveHistoryFilePath(
            options.Value.TestHistoryFilePath,
            environment.ContentRootPath
        );
        _upstreamPoolHistoryFilePath = ResolveHistoryFilePath(
            options.Value.UpstreamPoolTestHistoryFilePath,
            environment.ContentRootPath
        );
        LoadHistory();
        LoadUpstreamPoolHistory();
    }

    public IReadOnlyList<ProxyTestResponse> GetHistory(string? mode = null, Guid? resourceId = null)
    {
        lock (_historyGate)
        {
            IEnumerable<ProxyTestResponse> query = _history;

            if (!string.IsNullOrWhiteSpace(mode))
            {
                query = query.Where(item =>
                    string.Equals(item.Mode, mode, StringComparison.OrdinalIgnoreCase)
                );
            }

            if (resourceId.HasValue)
            {
                query = query.Where(item => item.ResourceId == resourceId.Value);
            }

            return query.ToArray();
        }
    }

    public IReadOnlyList<UpstreamPoolTestResponse> GetUpstreamPoolHistory(string? poolId = null)
    {
        lock (_upstreamPoolHistoryGate)
        {
            IEnumerable<UpstreamPoolTestResponse> query = _upstreamPoolHistory;

            if (!string.IsNullOrWhiteSpace(poolId))
            {
                query = query.Where(item =>
                    string.Equals(item.PoolId, poolId, StringComparison.OrdinalIgnoreCase)
                );
            }

            return query.ToArray();
        }
    }

    public bool DeleteUpstreamPoolTestRun(Guid runId)
    {
        UpstreamPoolTestResponse[] snapshot;

        lock (_upstreamPoolHistoryGate)
        {
            var node = _upstreamPoolHistory.First;
            while (node is not null && node.Value.RunId != runId)
            {
                node = node.Next;
            }

            if (node is null)
            {
                return false;
            }

            _upstreamPoolHistory.Remove(node);
            snapshot = _upstreamPoolHistory.ToArray();
        }

        PersistUpstreamPoolHistory(snapshot);
        return true;
    }

    public int ClearUpstreamPoolHistory(string poolId)
    {
        if (string.IsNullOrWhiteSpace(poolId))
        {
            throw new ArgumentException("上游池 ID 不能为空。", nameof(poolId));
        }

        UpstreamPoolTestResponse[] snapshot;
        var removed = 0;

        lock (_upstreamPoolHistoryGate)
        {
            var node = _upstreamPoolHistory.First;
            while (node is not null)
            {
                var next = node.Next;
                if (string.Equals(node.Value.PoolId, poolId, StringComparison.OrdinalIgnoreCase))
                {
                    _upstreamPoolHistory.Remove(node);
                    removed++;
                }

                node = next;
            }

            snapshot = _upstreamPoolHistory.ToArray();
        }

        PersistUpstreamPoolHistory(snapshot);
        return removed;
    }

    public async Task<ProxyTestResponse> TestTunnelAsync(
        ProxyTunnelResponse tunnel,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(tunnel.ForwardedProxy))
        {
            throw new InvalidOperationException("该代理尚未启动，无法测试。");
        }

        var logs = new List<ProxyTestLogEntry>();
        var forwardedProxy = ProxyEndpoint.Parse(tunnel.ForwardedProxy);

        Log(logs, "info", $"开始测试单个代理: {tunnel.ForwardedProxy}");
        var probe = await ProbeOnceAsync(forwardedProxy, cancellationToken).ConfigureAwait(false);
        Log(
            logs,
            "success",
            $"测试成功，出口 IP: {probe.ExitIp}，耗时: {probe.ElapsedMilliseconds} ms"
        );

        return Remember(
            new ProxyTestResponse(
                Guid.NewGuid(),
                DateTimeOffset.Now,
                "single",
                tunnel.Id,
                tunnel.RemoteProxyDisplay,
                tunnel.ForwardedProxy,
                true,
                1,
                0,
                probe.ExitIp,
                null,
                null,
                logs
            )
        );
    }

    public async Task<ProxyTestResponse> TestFixedProxyAsync(
        FixedProxyResponse fixedProxy,
        ProxyTestRequest? request,
        Func<Guid, FixedProxyResponse?> snapshotResolver,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(fixedProxy.ForwardedProxy))
        {
            throw new InvalidOperationException("该固定代理入口尚未启动，无法测试。");
        }

        var iterationCount = request?.IterationCount ?? 6;
        var intervalSeconds = request?.IntervalSeconds ?? 5;
        if (iterationCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "迭代次数必须大于 0。");
        }

        if (intervalSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "间隔秒数不能小于 0。");
        }

        var logs = new List<ProxyTestLogEntry>();
        var forwardedProxy = ProxyEndpoint.Parse(fixedProxy.ForwardedProxy);
        var observedExitIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var observedUpstreams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previousExitIp = null;
        string? previousUpstream = fixedProxy.LastSelectedUpstreamDisplay;
        string? lastExitIp = null;
        string? lastUpstream = fixedProxy.LastSelectedUpstreamDisplay;
        var successCount = 0;
        var failureCount = 0;
        var exitIpSwitchCount = 0;
        var upstreamSwitchCount = 0;

        Log(
            logs,
            "info",
            $"开始测试固定代理: {fixedProxy.ForwardedProxy}，轮数: {iterationCount}，间隔: {intervalSeconds} 秒"
        );

        for (var attempt = 1; attempt <= iterationCount; attempt++)
        {
            try
            {
                Log(logs, "info", $"第 {attempt}/{iterationCount} 轮开始测试。");
                var probe = await ProbeOnceAsync(forwardedProxy, cancellationToken)
                    .ConfigureAwait(false);
                successCount++;
                lastExitIp = probe.ExitIp;
                observedExitIps.Add(probe.ExitIp);

                var snapshot = snapshotResolver(fixedProxy.Id);
                var currentUpstream = snapshot?.LastSelectedUpstreamDisplay;
                if (!string.IsNullOrWhiteSpace(currentUpstream))
                {
                    observedUpstreams.Add(currentUpstream);
                }

                lastUpstream = currentUpstream ?? lastUpstream;
                var exitChanged =
                    previousExitIp is not null
                    && !string.Equals(
                        previousExitIp,
                        probe.ExitIp,
                        StringComparison.OrdinalIgnoreCase
                    );
                var upstreamChanged =
                    previousUpstream is not null
                    && !string.IsNullOrWhiteSpace(currentUpstream)
                    && !string.Equals(
                        previousUpstream,
                        currentUpstream,
                        StringComparison.OrdinalIgnoreCase
                    );

                if (exitChanged)
                {
                    exitIpSwitchCount++;
                }

                if (upstreamChanged)
                {
                    upstreamSwitchCount++;
                }

                Log(
                    logs,
                    "success",
                    $"第 {attempt}/{iterationCount} 轮成功，出口 IP: {probe.ExitIp}，耗时: {probe.ElapsedMilliseconds} ms"
                );
                Log(
                    logs,
                    "info",
                    $"最近上游: {currentUpstream ?? "未知"}，出口切换: {(exitChanged ? "是" : "否")}，上游切换: {(upstreamChanged ? "是" : "否")}"
                );

                previousExitIp = probe.ExitIp;
                previousUpstream = currentUpstream ?? previousUpstream;
            }
            catch (Exception ex)
            {
                failureCount++;
                Log(logs, "error", $"第 {attempt}/{iterationCount} 轮失败: {ex.Message}");
            }

            if (attempt < iterationCount && intervalSeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var switchSummary = new ProxyTestSwitchSummary(
            exitIpSwitchCount > 0,
            upstreamSwitchCount > 0,
            exitIpSwitchCount,
            upstreamSwitchCount,
            observedExitIps.Count,
            observedUpstreams.Count,
            successCount
        );

        return Remember(
            new ProxyTestResponse(
                Guid.NewGuid(),
                DateTimeOffset.Now,
                "fixed",
                fixedProxy.Id,
                fixedProxy.Note ?? fixedProxy.PoolId,
                fixedProxy.ForwardedProxy,
                failureCount == 0,
                successCount,
                failureCount,
                lastExitIp,
                lastUpstream,
                switchSummary,
                logs
            )
        );
    }

    public async Task<UpstreamProxyTestItemResponse> TestUpstreamProxyAsync(
        Guid upstreamId,
        string proxyDisplay,
        ProxyEndpoint proxy,
        CancellationToken cancellationToken
    )
    {
        var testedAt = DateTimeOffset.Now;

        try
        {
            var probe = await ProbeOnceAsync(proxy, cancellationToken).ConfigureAwait(false);

            return new UpstreamProxyTestItemResponse(
                upstreamId,
                proxyDisplay,
                true,
                probe.ExitIp,
                probe.ElapsedMilliseconds,
                null,
                testedAt
            );
        }
        catch (Exception ex)
        {
            return new UpstreamProxyTestItemResponse(
                upstreamId,
                proxyDisplay,
                false,
                null,
                null,
                ex.Message,
                testedAt
            );
        }
    }

    public UpstreamPoolTestResponse RememberUpstreamPoolTest(UpstreamPoolTestResponse response)
    {
        UpstreamPoolTestResponse[] snapshot;

        lock (_upstreamPoolHistoryGate)
        {
            _upstreamPoolHistory.AddFirst(response);
            while (_upstreamPoolHistory.Count > HistoryLimit)
            {
                _upstreamPoolHistory.RemoveLast();
            }

            snapshot = _upstreamPoolHistory.ToArray();
        }

        PersistUpstreamPoolHistory(snapshot);
        return response;
    }

    private ProxyTestResponse Remember(ProxyTestResponse response)
    {
        ProxyTestResponse[] snapshot;

        lock (_historyGate)
        {
            _history.AddFirst(response);
            while (_history.Count > HistoryLimit)
            {
                _history.RemoveLast();
            }

            snapshot = _history.ToArray();
        }

        PersistHistory(snapshot);

        return response;
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(_historyFilePath))
            {
                return;
            }

            var json = File.ReadAllText(_historyFilePath);
            var items = JsonSerializer.Deserialize<List<ProxyTestResponse>>(json, JsonOptions);
            if (items is null || items.Count == 0)
            {
                return;
            }

            foreach (
                var item in items
                    .OrderByDescending(static entry => entry.CompletedAt)
                    .Take(HistoryLimit)
            )
            {
                _history.AddLast(item);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载测试历史文件失败: {Path}", _historyFilePath);
        }
    }

    private void LoadUpstreamPoolHistory()
    {
        try
        {
            if (!File.Exists(_upstreamPoolHistoryFilePath))
            {
                return;
            }

            var json = File.ReadAllText(_upstreamPoolHistoryFilePath);
            var items = JsonSerializer.Deserialize<List<UpstreamPoolTestResponse>>(
                json,
                JsonOptions
            );
            if (items is null || items.Count == 0)
            {
                return;
            }

            foreach (
                var item in items
                    .OrderByDescending(static entry => entry.CompletedAt)
                    .Take(HistoryLimit)
            )
            {
                _upstreamPoolHistory.AddLast(item);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "加载上游池测试历史文件失败: {Path}",
                _upstreamPoolHistoryFilePath
            );
        }
    }

    private void PersistHistory(IReadOnlyList<ProxyTestResponse> snapshot)
    {
        try
        {
            var directory = Path.GetDirectoryName(_historyFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempFilePath = _historyFilePath + ".tmp";
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(tempFilePath, json);
            File.Move(tempFilePath, _historyFilePath, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存测试历史文件失败: {Path}", _historyFilePath);
        }
    }

    private void PersistUpstreamPoolHistory(IReadOnlyList<UpstreamPoolTestResponse> snapshot)
    {
        try
        {
            var directory = Path.GetDirectoryName(_upstreamPoolHistoryFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempFilePath = _upstreamPoolHistoryFilePath + ".tmp";
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(tempFilePath, json);
            File.Move(tempFilePath, _upstreamPoolHistoryFilePath, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "保存上游池测试历史文件失败: {Path}",
                _upstreamPoolHistoryFilePath
            );
        }
    }

    private static string ResolveHistoryFilePath(string configuredPath, string contentRootPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? "App_Data/test-history.json"
            : configuredPath.Trim();

        return Path.IsPathRooted(path) ? path : Path.Combine(contentRootPath, path);
    }

    private static async Task<ProxyProbeResult> ProbeOnceAsync(
        ProxyEndpoint proxy,
        CancellationToken cancellationToken
    )
    {
        using var handler = CreateHandler(proxy);
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        using var response = await httpClient
            .GetAsync(ProbeUri, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = (
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
        ).Trim();
        stopwatch.Stop();

        return new ProxyProbeResult(body, startedAt, stopwatch.ElapsedMilliseconds);
    }

    private static HttpMessageHandler CreateHandler(ProxyEndpoint proxy)
    {
        return proxy.Protocol switch
        {
            ProxyProtocol.Http => CreateHttpHandler(proxy),
            ProxyProtocol.Socks5 => CreateSocksHandler(proxy),
            _ => throw new InvalidOperationException($"不支持的代理协议: {proxy.Protocol}"),
        };
    }

    private static HttpMessageHandler CreateHttpHandler(ProxyEndpoint proxy)
    {
        var webProxy = new WebProxy(proxy.ProxyUri);
        if (proxy.HasCredentials)
        {
            webProxy.Credentials = new NetworkCredential(proxy.UserName, proxy.Password);
        }

        return new HttpClientHandler
        {
            Proxy = webProxy,
            UseProxy = true,
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
    }

    private static HttpMessageHandler CreateSocksHandler(ProxyEndpoint proxy)
    {
        var socksWebProxy = new WebProxy(proxy.ProxyUri);
        if (proxy.HasCredentials)
        {
            socksWebProxy.Credentials = new NetworkCredential(proxy.UserName, proxy.Password);
        }

        return new SocketsHttpHandler
        {
            Proxy = socksWebProxy,
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            },
        };
    }

    private static void Log(List<ProxyTestLogEntry> logs, string level, string message)
    {
        logs.Add(new ProxyTestLogEntry(DateTimeOffset.Now, level, message));
    }

    private sealed record ProxyProbeResult(
        string ExitIp,
        DateTimeOffset StartedAt,
        long ElapsedMilliseconds
    );
}
