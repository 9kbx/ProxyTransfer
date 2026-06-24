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
var proxyEndpoint = LoadFirstProxy(proxyFilePath);

logger.LogInformation("[配置] 代理文件: {ProxyFilePath}", proxyFilePath);
logger.LogInformation("[配置] 使用代理: {ProxyUri}", proxyEndpoint.SafeDisplayUri);

logger.LogInformation(
    "[代理] 开始绑定上游代理到本地 HTTP 中转: {ProxyUri}",
    proxyEndpoint.SafeDisplayUri
);
var tunnelStartTimestamp = DateTimeOffset.Now;
var tunnelStartStopwatch = Stopwatch.StartNew();

await using var tunnel = await Socks5ProxyTunnel.StartAsync(proxyEndpoint);

tunnelStartStopwatch.Stop();
logger.LogInformation(
    "[代理] 本地中转服务已启动: {LocalProxyUri}，开始时间: {StartedAt:HH:mm:ss.fff}，绑定耗时: {ElapsedMs} ms",
    tunnel.LocalProxyUri,
    tunnelStartTimestamp,
    tunnelStartStopwatch.ElapsedMilliseconds
);

try
{
    await proxyTunnelDemo.RunAsync(tunnel);

    if (proxyEndpoint.IsSocks5)
    {
        await socks5HttpClientDemo.RunAsync(
            proxyEndpoint.Host,
            proxyEndpoint.Port,
            proxyEndpoint.UserName ?? string.Empty,
            proxyEndpoint.Password ?? string.Empty
        );
    }
    else
    {
        logger.LogInformation(
            "[HttpClient:SOCKS5] 已跳过，当前上游代理不是 SOCKS5: {ProxyUri}",
            proxyEndpoint.SafeDisplayUri
        );
    }

    await socks5ProxyPuppeteerDemo.RunAsync(tunnel);
}
finally
{
    logger.LogInformation("[代理] 开始关闭本地中转服务: {LocalProxyUri}", tunnel.LocalProxyUri);
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

static ProxyEndpoint LoadFirstProxy(string proxyFilePath)
{
    var candidates = File.ReadLines(proxyFilePath)
        .Select((line, index) => (Line: line.Trim(), LineNumber: index + 1))
        .Where(static item =>
            !string.IsNullOrWhiteSpace(item.Line)
            && !item.Line.StartsWith("#", StringComparison.Ordinal)
        )
        .ToArray();

    foreach (var candidate in candidates)
    {
        if (TryParseProxyEndpoint(candidate.Line, out var endpoint))
        {
            return endpoint;
        }
    }

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
