using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProxyTransfer.Tunnel;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
    options.SingleLine = true;
});

builder.Services.AddSingleton<ProxyTunnelDemo>();
builder.Services.AddSingleton<Socks5HttpClientDemo>();
builder.Services.AddSingleton<Socks5ProxyPuppeteerDemo>();

using var host = builder.Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program");
var proxyTunnelDemo = host.Services.GetRequiredService<ProxyTunnelDemo>();
var socks5HttpClientDemo = host.Services.GetRequiredService<Socks5HttpClientDemo>();
var socks5ProxyPuppeteerDemo = host.Services.GetRequiredService<Socks5ProxyPuppeteerDemo>();

var proxyFilePath = ResolveProxyFilePath();
var proxyEndpoints = LoadProxyEndpoints(proxyFilePath);
var httpProxyEndpoint = proxyEndpoints.First(x => x.IsHttp);
var socks5ProxyEndpoint = proxyEndpoints.First(x => x.IsSocks5);

logger.LogInformation("[配置] 代理文件: {ProxyFilePath}", proxyFilePath);
logger.LogInformation("[配置] 使用代理: {ProxyUri}", httpProxyEndpoint.SafeDisplayUri);

logger.LogInformation(
    "[代理] 开始绑定上游代理到本地 HTTP 中转: {ProxyUri}",
    httpProxyEndpoint.SafeDisplayUri
);

var tunnelStartTimestamp = DateTimeOffset.Now;
var tunnelStartStopwatch = Stopwatch.StartNew();

await using var httpTunnel = await HttpProxyTunnel.StartAsync(httpProxyEndpoint);
await using var socks5Tunnel = await Socks5ProxyTunnel.StartAsync(socks5ProxyEndpoint);

tunnelStartStopwatch.Stop();
logger.LogInformation(
    "[代理] 本地 HTTP 中转服务已启动: {LocalProxyUri}，开始时间: {StartedAt:HH:mm:ss.fff}，绑定耗时: {ElapsedMs} ms",
    httpTunnel.LocalProxyUri,
    tunnelStartTimestamp,
    tunnelStartStopwatch.ElapsedMilliseconds
);

try
{
    await proxyTunnelDemo.RunAsync(httpTunnel);

    if (socks5ProxyEndpoint.IsSocks5)
    {
        logger.LogInformation(
            "[代理] 本地 SOCKS5 中转服务已启动: {LocalProxyUri} -> {RemoteProxyUri}",
            socks5Tunnel.LocalProxyUri,
            socks5Tunnel.RemoteProxyUri
        );

        await socks5HttpClientDemo.RunAsync(
            socks5Tunnel.PublicHost,
            socks5Tunnel.LocalPort,
            string.Empty,
            string.Empty
        );

        await socks5HttpClientDemo.RunAsync(
            socks5ProxyEndpoint.Host,
            socks5ProxyEndpoint.Port,
            socks5ProxyEndpoint.UserName ?? string.Empty,
            socks5ProxyEndpoint.Password ?? string.Empty
        );

        await socks5ProxyPuppeteerDemo.RunAsync(socks5Tunnel);
    }
    else
    {
        logger.LogInformation(
            "[HttpClient:SOCKS5] 已跳过，当前上游代理不是 SOCKS5: {ProxyUri}",
            socks5ProxyEndpoint.SafeDisplayUri
        );
    }
}
finally
{
    logger.LogInformation("[代理] 开始关闭本地中转服务: {LocalProxyUri}", httpTunnel.LocalProxyUri);
    logger.LogInformation(
        "[代理] 开始关闭本地中转服务: {LocalProxyUri}",
        socks5Tunnel.LocalProxyUri
    );
}

static string ResolveProxyFilePath()
{
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
        "未找到 proxy.txt，请将文件放在 Demo 项目目录或输出目录。",
        "proxy.txt"
    );
}

static List<ProxyEndpoint> LoadProxyEndpoints(string proxyFilePath)
{
    var candidates = File.ReadLines(proxyFilePath)
        .Select((line, index) => (Line: line.Trim(), LineNumber: index + 1))
        .Where(static item =>
            !string.IsNullOrWhiteSpace(item.Line)
            && !item.Line.StartsWith("#", StringComparison.Ordinal)
        )
        .ToArray();

    var endpoints = new List<ProxyEndpoint>();
    foreach (var candidate in candidates)
    {
        if (TryParseProxyEndpoint(candidate.Line, out var endpoint))
        {
            endpoints.Add(endpoint);
        }
    }

    if (endpoints.Count > 0)
        return endpoints;

    throw new InvalidOperationException(
        "proxy.txt 中没有可用代理。请至少提供一行 http://user:pass@host:port、socks5://user:pass@host:port，或省略 scheme 的 user:pass@host:port。"
    );
}

static bool TryParseProxyEndpoint(string value, out ProxyEndpoint endpoint)
{
    try
    {
        endpoint = ProxyEndpoint.Parse(value);
        return true;
    }
    catch (FormatException) { }
    catch (ArgumentException) { }

    endpoint = default!;
    return false;
}
