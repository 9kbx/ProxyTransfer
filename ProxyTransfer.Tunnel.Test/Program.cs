using System.Diagnostics;
using System.Net;
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

using var host = builder.Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program");
var tester = host.Services.GetRequiredService<ForwardedProxySmokeTester>();

var proxyFilePath = ResolveProxyFilePath(args);
var proxies = LoadProxies(proxyFilePath);

logger.LogInformation("[配置] 测试文件: {ProxyFilePath}", proxyFilePath);
logger.LogInformation("[配置] 待测试代理数量: {ProxyCount}", proxies.Count);

var failures = 0;
for (var index = 0; index < proxies.Count; index++)
{
    var proxy = proxies[index];

    try
    {
        await tester.RunAsync(proxy, index + 1, proxies.Count);
    }
    catch (Exception ex)
    {
        failures++;
        logger.LogError(ex, "[失败] 代理测试失败: {ProxyUri}", proxy.SafeDisplayUri);
    }
}

logger.LogInformation(
    "[完成] 测试结束，总数: {TotalCount}，成功: {SuccessCount}，失败: {FailureCount}",
    proxies.Count,
    proxies.Count - failures,
    failures
);

Environment.ExitCode = failures == 0 ? 0 : 1;

static string ResolveProxyFilePath(string[] args)
{
    if (args.Length > 0)
    {
        var directPath = Path.GetFullPath(args[0]);
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
        "未找到 proxy.txt，请将前端复制的下游代理列表保存为该文件，或在命令行上传入文件路径。",
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
        "proxy.txt 中没有可用下游代理。请粘贴前端复制出的 http://host:port 或 socks5://host:port。"
    );
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

    public async Task RunAsync(
        ProxyEndpoint proxy,
        int currentIndex,
        int totalCount,
        CancellationToken cancellationToken = default
    )
    {
        using var handler = CreateHandler(proxy);

        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        _logger.LogInformation(
            "[测试] ({CurrentIndex}/{TotalCount}) 开始验证 {ProxyUri}",
            currentIndex,
            totalCount,
            proxy.SafeDisplayUri
        );

        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        using var response = await httpClient.GetAsync(ProbeUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        stopwatch.Stop();

        _logger.LogInformation(
            "[成功] ({CurrentIndex}/{TotalCount}) {ProxyUri} 出口 IP: {Ip}，开始时间: {StartedAt:HH:mm:ss.fff}，耗时: {ElapsedMs} ms",
            currentIndex,
            totalCount,
            proxy.SafeDisplayUri,
            body,
            startedAt,
            stopwatch.ElapsedMilliseconds
        );
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

        return new HttpClientHandler { Proxy = webProxy, UseProxy = true };
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
        };
    }

    private static string FormatHost(string host) =>
        host.Contains(':', StringComparison.Ordinal)
        && !host.StartsWith("[", StringComparison.Ordinal)
            ? $"[{host}]"
            : host;
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
