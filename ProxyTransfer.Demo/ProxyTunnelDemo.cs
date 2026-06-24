using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using ProxyTransfer.Tunnel;

public class ProxyTunnelDemo
{
    private readonly ILogger<ProxyTunnelDemo> _logger;

    public ProxyTunnelDemo(ILogger<ProxyTunnelDemo> logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(
        Socks5ProxyTunnel tunnel,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation(
            "[代理] 使用本地 HTTP 中转访问目标网站: {LocalProxyUri}",
            tunnel.LocalProxyUri
        );

        var proxy = new WebProxy(tunnel.LocalProxyUri);

        using var handler = new SocketsHttpHandler { Proxy = proxy, UseProxy = true };

        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        _logger.LogInformation(
            "[HttpClient:Tunnel] 开始请求 https://api.ipify.org/，代理: {ProxyUri} -> {RemoteProxyUri}",
            tunnel.LocalProxyUri,
            tunnel.RemoteProxyUri
        );
        var requestStartedAt = DateTimeOffset.Now;
        var requestStopwatch = Stopwatch.StartNew();
        using var response = await httpClient.GetAsync("https://api.ipify.org/", cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        requestStopwatch.Stop();
        _logger.LogInformation(
            "[HttpClient:Tunnel] 请求完成，开始时间: {StartedAt:HH:mm:ss.fff}，耗时: {ElapsedMs} ms，出口 IP: {Ip}",
            requestStartedAt,
            requestStopwatch.ElapsedMilliseconds,
            content.Trim()
        );
    }
}
