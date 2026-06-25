using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
    options.SingleLine = true;
});

builder.Services.AddSingleton<ForwardedProxySmokeTester>();
builder.Services.AddSingleton<FixedProxyApiClient>();

using var host = builder.Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program");
var tester = host.Services.GetRequiredService<ForwardedProxySmokeTester>();
var apiClient = host.Services.GetRequiredService<FixedProxyApiClient>();

var options = TestOptions.Parse(args);

switch (options.Mode)
{
    case TestMode.SingleProxy:
        await RunSingleProxyTestAsync(options, tester, logger);
        break;
    case TestMode.FixedProxy:
        await RunFixedProxyTestAsync(options, tester, apiClient, logger);
        break;
    case TestMode.MultiThreadProxy:
        await RunMultiThreadProxyTestAsync(options, tester, logger);
        break;
    default:
        throw new InvalidOperationException($"未知的测试模式: {options.Mode}");
}

static async Task RunSingleProxyTestAsync(
    TestOptions options,
    ForwardedProxySmokeTester tester,
    ILogger logger,
    CancellationToken cancellationToken = default
)
{
    if (!string.IsNullOrWhiteSpace(options.ProxySource) && LooksLikeProxyUri(options.ProxySource))
    {
        var proxy = ParseProxy(options.ProxySource.Trim(), 1);

        logger.LogInformation("[模式] 单代理一对一测试");
        logger.LogInformation("[配置] 目标代理: {ProxyUri}", proxy.SafeDisplayUri);

        var result = await tester.RunOnceAsync(proxy, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "[完成] 单代理测试完成，代理: {ProxyUri}，出口 IP: {Ip}，耗时: {ElapsedMs} ms",
            proxy.SafeDisplayUri,
            result.ExitIp,
            result.ElapsedMilliseconds
        );

        Environment.ExitCode = 0;
        return;
    }

    var proxyFilePath = ResolveProxyFilePath(options.ProxySource);
    var proxies = LoadProxies(proxyFilePath);
    var failures = 0;

    logger.LogInformation("[模式] proxy.txt 批量测试");
    logger.LogInformation("[配置] 代理文件: {ProxyFilePath}", proxyFilePath);
    logger.LogInformation("[配置] 代理数量: {ProxyCount}", proxies.Count);

    for (var index = 0; index < proxies.Count; index++)
    {
        var proxy = proxies[index];

        try
        {
            var result = await tester.RunOnceAsync(proxy, cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "[完成] ({Index}/{Total}) 代理: {ProxyUri}，出口 IP: {Ip}，耗时: {ElapsedMs} ms",
                index + 1,
                proxies.Count,
                proxy.SafeDisplayUri,
                result.ExitIp,
                result.ElapsedMilliseconds
            );
        }
        catch (Exception ex)
        {
            failures++;
            logger.LogError(
                ex,
                "[失败] ({Index}/{Total}) 代理测试失败: {ProxyUri}",
                index + 1,
                proxies.Count,
                proxy.SafeDisplayUri
            );
        }
    }

    logger.LogInformation(
        "[完成] proxy.txt 批量测试结束，总数: {TotalCount}，成功: {SuccessCount}，失败: {FailureCount}",
        proxies.Count,
        proxies.Count - failures,
        failures
    );

    Environment.ExitCode = failures == 0 ? 0 : 1;
}

async Task RunFixedProxyTestAsync(
    TestOptions options,
    ForwardedProxySmokeTester tester,
    FixedProxyApiClient apiClient,
    ILogger logger,
    CancellationToken cancellationToken = default
)
{
    var proxy = ResolveSingleProxy(options.ProxySource);

    logger.LogInformation("[模式] 固定下游代理动态切换观察测试");
    logger.LogInformation("[配置] 固定下游代理: {ProxyUri}", proxy.SafeDisplayUri);
    logger.LogInformation(
        "[配置] 轮询次数: {IterationCount}，间隔: {IntervalSeconds} 秒",
        options.IterationCount,
        options.IntervalSeconds
    );

    FixedProxySnapshot? snapshot = null;
    if (!string.IsNullOrWhiteSpace(options.ApiBaseUrl))
    {
        try
        {
            snapshot = await apiClient
                .TryResolveSnapshotAsync(
                    options.ApiBaseUrl!,
                    options.FixedProxyId,
                    proxy.ProxyUri,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex)
            when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(
                ex,
                "[提示] 访问固定入口 API 失败，将只观察出口 IP 变化。API: {ApiBaseUrl}",
                options.ApiBaseUrl
            );
            snapshot = null;
        }

        if (snapshot is null)
        {
            logger.LogWarning(
                "[提示] 未能从 API 找到与 {ProxyUri} 对应的固定入口，将只观察出口 IP 变化。",
                proxy.ProxyUri
            );
        }
        else
        {
            logger.LogInformation(
                "[配置] 固定入口 ID: {FixedProxyId}，池: {PoolId}，粘性: {StickyMinutes} 分钟，最近上游: {LastSelectedUpstream}",
                snapshot.Id,
                snapshot.PoolId,
                snapshot.StickyMinutes,
                snapshot.LastSelectedUpstreamDisplay ?? "尚未选择"
            );

            var plannedDuration = TimeSpan.FromSeconds(
                Math.Max(0, options.IterationCount - 1) * options.IntervalSeconds
            );
            var stickyWindow = TimeSpan.FromMinutes(snapshot.StickyMinutes);
            if (plannedDuration < stickyWindow)
            {
                logger.LogWarning(
                    "[提示] 当前计划观察时长 {PlannedDuration} 小于粘性窗口 {StickyWindow}；若上游不中断，测试期间可能看不到自动切换。",
                    plannedDuration,
                    stickyWindow
                );
            }
        }
    }

    string? previousExitIp = null;
    string? previousUpstream = snapshot?.LastSelectedUpstreamDisplay;
    var failures = 0;

    for (var attempt = 1; attempt <= options.IterationCount; attempt++)
    {
        try
        {
            var result = await tester.RunOnceAsync(proxy, cancellationToken).ConfigureAwait(false);

            snapshot = await TryResolveSnapshotSafelyAsync(
                    options,
                    proxy,
                    apiClient,
                    logger,
                    cancellationToken
                )
                .ConfigureAwait(false);

            var currentUpstream = snapshot?.LastSelectedUpstreamDisplay;
            var exitChanged =
                previousExitIp is not null
                && !string.Equals(
                    previousExitIp,
                    result.ExitIp,
                    StringComparison.OrdinalIgnoreCase
                );
            var upstreamChanged =
                previousUpstream is not null
                && !string.Equals(
                    previousUpstream,
                    currentUpstream,
                    StringComparison.OrdinalIgnoreCase
                );

            logger.LogInformation(
                "[观察] ({Attempt}/{Total}) 出口 IP: {ExitIp}，最近上游: {CurrentUpstream}，出口切换: {ExitChanged}，上游切换: {UpstreamChanged}",
                attempt,
                options.IterationCount,
                result.ExitIp,
                currentUpstream ?? "未知",
                exitChanged ? "是" : "否",
                upstreamChanged ? "是" : "否"
            );

            previousExitIp = result.ExitIp;
            previousUpstream = currentUpstream ?? previousUpstream;
        }
        catch (Exception ex)
        {
            failures++;
            logger.LogError(
                ex,
                "[失败] ({Attempt}/{Total}) 固定下游代理测试失败。",
                attempt,
                options.IterationCount
            );
        }

        if (attempt < options.IterationCount)
        {
            await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    logger.LogInformation(
        "[完成] 固定代理观察结束，总轮数: {TotalCount}，成功: {SuccessCount}，失败: {FailureCount}，最后上游: {LastUpstream}",
        options.IterationCount,
        options.IterationCount - failures,
        failures,
        previousUpstream ?? "未知"
    );

    Environment.ExitCode = failures == 0 ? 0 : 1;
}

static async Task RunMultiThreadProxyTestAsync(
    TestOptions options,
    ForwardedProxySmokeTester tester,
    ILogger logger,
    CancellationToken cancellationToken = default
)
{
    var proxy = ResolveSingleProxy(options.ProxySource);

    logger.LogInformation("[模式] 多线程并发代理测试");
    logger.LogInformation("[配置] 目标代理: {ProxyUri}", proxy.SafeDisplayUri);
    logger.LogInformation("[配置] 并发线程数: {ThreadCount}", options.ThreadCount);

    var workers = new Task<bool>[options.ThreadCount];
    for (var workerIndex = 0; workerIndex < workers.Length; workerIndex++)
    {
        var workerId = workerIndex + 1;
        workers[workerIndex] = ExecuteWorkerAsync(workerId);
    }

    var results = await Task.WhenAll(workers).ConfigureAwait(false);
    var successCount = results.Count(static item => item);
    var failureCount = results.Length - successCount;

    logger.LogInformation(
        "[完成] 多线程并发代理测试结束，总线程数: {TotalCount}，成功: {SuccessCount}，失败: {FailureCount}",
        results.Length,
        successCount,
        failureCount
    );

    Environment.ExitCode = failureCount == 0 ? 0 : 1;

    async Task<bool> ExecuteWorkerAsync(int workerId)
    {
        try
        {
            var result = await tester.RunOnceAsync(proxy, cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "[线程 {WorkerId}] 成功，出口 IP: {Ip}，耗时: {ElapsedMs} ms",
                workerId,
                result.ExitIp,
                result.ElapsedMilliseconds
            );
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[线程 {WorkerId}] 并发代理测试失败。", workerId);
            return false;
        }
    }
}

async Task<FixedProxySnapshot?> TryResolveSnapshotSafelyAsync(
    TestOptions options,
    ProxyEndpoint proxy,
    FixedProxyApiClient apiClient,
    ILogger logger,
    CancellationToken cancellationToken
)
{
    if (string.IsNullOrWhiteSpace(options.ApiBaseUrl))
    {
        return null;
    }

    try
    {
        return await apiClient
            .TryResolveSnapshotAsync(
                options.ApiBaseUrl!,
                options.FixedProxyId,
                proxy.ProxyUri,
                cancellationToken
            )
            .ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
    {
        logger.LogWarning(
            ex,
            "[提示] 获取固定入口状态失败，本轮仅观察出口 IP。API: {ApiBaseUrl}",
            options.ApiBaseUrl
        );
        return null;
    }
}

static ProxyEndpoint ResolveSingleProxy(string? source)
{
    if (!string.IsNullOrWhiteSpace(source) && LooksLikeProxyUri(source))
    {
        return ParseProxy(source.Trim(), 1);
    }

    var proxyFilePath = ResolveProxyFilePath(source);
    var proxies = LoadProxies(proxyFilePath);
    return proxies[0];
}

static string ResolveProxyFilePath(string? source)
{
    if (!string.IsNullOrWhiteSpace(source))
    {
        var directPath = Path.GetFullPath(source);
        if (File.Exists(directPath))
        {
            return directPath;
        }

        throw new FileNotFoundException("未找到指定的代理文件。", directPath);
    }

    var candidatePaths = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), "proxy.txt"),
        Path.Combine(AppContext.BaseDirectory, "proxy.txt"),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "proxy.txt")),
    };

    foreach (var candidatePath in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        if (File.Exists(candidatePath))
        {
            return candidatePath;
        }
    }

    throw new FileNotFoundException(
        "未找到 proxy.txt，请把要测试的下游代理写入文件，或在命令行上传入文件路径/直接传入代理地址。",
        "proxy.txt"
    );
}

static List<ProxyEndpoint> LoadProxies(string proxyFilePath)
{
    var proxies = File.ReadLines(proxyFilePath)
        .Select((line, index) => (Line: line.Trim(), LineNumber: index + 1))
        .Where(static item =>
            !string.IsNullOrWhiteSpace(item.Line)
            && !item.Line.StartsWith("#", StringComparison.Ordinal)
        )
        .Select(static item => ParseProxy(item.Line, item.LineNumber))
        .DistinctBy(static item => item.ProxyUri, StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (proxies.Count > 0)
    {
        return proxies;
    }

    throw new InvalidOperationException(
        "proxy.txt 中没有可用下游代理。请至少提供一条 http://host:port 或 socks5://host:port。"
    );
}

static bool LooksLikeProxyUri(string value)
{
    return value.Contains("://", StringComparison.Ordinal)
        || value.StartsWith("127.", StringComparison.Ordinal)
        || value.StartsWith("localhost", StringComparison.OrdinalIgnoreCase);
}

static ProxyEndpoint ParseProxy(string line, int lineNumber)
{
    try
    {
        return ProxyEndpoint.Parse(line);
    }
    catch (Exception ex) when (ex is FormatException or ArgumentException)
    {
        throw new FormatException($"第 {lineNumber} 行代理格式无效: {line}", ex);
    }
}

internal sealed class ForwardedProxySmokeTester
{
    private static readonly Uri ProbeUri = new("https://api.ipify.org/");

    private readonly ILogger<ForwardedProxySmokeTester> _logger;

    public ForwardedProxySmokeTester(ILogger<ForwardedProxySmokeTester> logger)
    {
        _logger = logger;
    }

    public async Task<ProxyProbeResult> RunOnceAsync(
        ProxyEndpoint proxy,
        CancellationToken cancellationToken = default
    )
    {
        using var handler = CreateHandler(proxy);

        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        _logger.LogInformation("[测试] 开始验证 {ProxyUri}", proxy.SafeDisplayUri);

        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        using var response = await httpClient.GetAsync(ProbeUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        stopwatch.Stop();

        _logger.LogInformation(
            "[成功] {ProxyUri} 出口 IP: {Ip}，开始时间: {StartedAt:HH:mm:ss.fff}，耗时: {ElapsedMs} ms",
            proxy.SafeDisplayUri,
            body,
            startedAt,
            stopwatch.ElapsedMilliseconds
        );

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
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
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
            SslOptions = new SslClientAuthenticationOptions()
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        };
    }
}

internal sealed record ProxyProbeResult(
    string ExitIp,
    DateTimeOffset StartedAt,
    long ElapsedMilliseconds
);

internal sealed class FixedProxyApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<FixedProxySnapshot?> TryResolveSnapshotAsync(
        string apiBaseUrl,
        Guid? fixedProxyId,
        string forwardedProxy,
        CancellationToken cancellationToken = default
    )
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        httpClient.DefaultRequestHeaders.Add("x-apikey", "change-me");
        var normalizedBaseUrl = apiBaseUrl.TrimEnd('/');
        using var response = await httpClient
            .GetAsync($"{normalizedBaseUrl}/api/fixed-proxies", cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var stream = await response
            .Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var items = await JsonSerializer
            .DeserializeAsync<List<FixedProxySnapshot>>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (items is null)
        {
            return null;
        }

        if (fixedProxyId.HasValue)
        {
            return items.FirstOrDefault(item => item.Id == fixedProxyId.Value);
        }

        return items.FirstOrDefault(item =>
            string.Equals(item.ForwardedProxy, forwardedProxy, StringComparison.OrdinalIgnoreCase)
        );
    }
}

internal sealed record FixedProxySnapshot(
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

internal enum TestMode
{
    SingleProxy,
    FixedProxy,
    MultiThreadProxy,
}

internal sealed record TestOptions(
    TestMode Mode,
    string? ProxySource,
    string? ApiBaseUrl,
    Guid? FixedProxyId,
    int IterationCount,
    int IntervalSeconds,
    int ThreadCount
)
{
    public static TestOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new TestOptions(TestMode.SingleProxy, null, null, null, 8, 8, 2);
        }

        var mode = TestMode.SingleProxy;
        var index = 0;
        if (string.Equals(args[0], "single", StringComparison.OrdinalIgnoreCase))
        {
            mode = TestMode.SingleProxy;
            index = 1;
        }
        else if (string.Equals(args[0], "fixed", StringComparison.OrdinalIgnoreCase))
        {
            mode = TestMode.FixedProxy;
            index = 1;
        }
        else if (
            string.Equals(args[0], "multi", StringComparison.OrdinalIgnoreCase)
            || string.Equals(args[0], "multi-thread", StringComparison.OrdinalIgnoreCase)
        )
        {
            mode = TestMode.MultiThreadProxy;
            index = 1;
        }

        string? proxySource = null;
        string? apiBaseUrl = mode == TestMode.FixedProxy ? "http://127.0.0.1:5080" : null;
        Guid? fixedProxyId = null;
        var iterationCount = 8;
        var intervalSeconds = 8;
        var threadCount = 2;

        while (index < args.Length)
        {
            var current = args[index];
            switch (current)
            {
                case "--api-base-url":
                    index++;
                    apiBaseUrl = RequireValue(args, index, current);
                    break;
                case "--fixed-id":
                    index++;
                    fixedProxyId = Guid.Parse(RequireValue(args, index, current));
                    break;
                case "--count":
                    index++;
                    iterationCount = int.Parse(RequireValue(args, index, current));
                    break;
                case "--interval-seconds":
                    index++;
                    intervalSeconds = int.Parse(RequireValue(args, index, current));
                    break;
                case "--threads":
                case "--thread-count":
                    index++;
                    threadCount = int.Parse(RequireValue(args, index, current));
                    break;
                default:
                    if (proxySource is null)
                    {
                        proxySource = current;
                    }
                    else
                    {
                        throw new ArgumentException($"无法识别的参数: {current}");
                    }

                    break;
            }

            index++;
        }

        if (iterationCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "--count 必须大于 0。");
        }

        if (intervalSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "--interval-seconds 不能小于 0。");
        }

        if (threadCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "--threads 必须大于 0。");
        }

        return new TestOptions(
            mode,
            proxySource,
            apiBaseUrl,
            fixedProxyId,
            iterationCount,
            intervalSeconds,
            threadCount
        );
    }

    private static string RequireValue(string[] args, int index, string option)
    {
        if (index >= args.Length)
        {
            throw new ArgumentException($"参数 {option} 缺少值。");
        }

        return args[index];
    }
}

internal enum ProxyProtocol
{
    Http,
    Socks5,
}

internal sealed record ProxyEndpoint(
    ProxyProtocol Protocol,
    string Host,
    int Port,
    string? UserName,
    string? Password
)
{
    public bool HasCredentials => !string.IsNullOrWhiteSpace(UserName);

    public string ProxyUri => BuildUri(maskPassword: false);

    public string SafeDisplayUri => BuildUri(maskPassword: true);

    public static ProxyEndpoint Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException("代理地址不能为空。");
        }

        var normalized = value.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = $"http://{normalized}";
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            throw new FormatException($"无法解析代理地址: {value}");
        }

        var protocol = uri.Scheme.ToLowerInvariant() switch
        {
            "http" => ProxyProtocol.Http,
            "socks5" => ProxyProtocol.Socks5,
            _ => throw new FormatException($"不支持的代理协议: {uri.Scheme}"),
        };

        if (string.IsNullOrWhiteSpace(uri.Host) || uri.Port <= 0)
        {
            throw new FormatException($"代理地址缺少 host 或 port: {value}");
        }

        string? userName = null;
        string? password = null;
        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            userName = Uri.UnescapeDataString(parts[0]);
            password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }

        return new ProxyEndpoint(protocol, uri.Host, uri.Port, userName, password);
    }

    private string BuildUri(bool maskPassword)
    {
        var scheme = Protocol == ProxyProtocol.Socks5 ? "socks5" : "http";
        var credentials = string.Empty;

        if (HasCredentials)
        {
            var password = maskPassword ? "***" : Password ?? string.Empty;
            credentials = $"{Uri.EscapeDataString(UserName!)}:{Uri.EscapeDataString(password)}@";
        }

        return $"{scheme}://{credentials}{FormatHost(Host)}:{Port}";
    }

    private static string FormatHost(string host) =>
        host.Contains(':', StringComparison.Ordinal)
        && !host.StartsWith("[", StringComparison.Ordinal)
            ? $"[{host}]"
            : host;
}
