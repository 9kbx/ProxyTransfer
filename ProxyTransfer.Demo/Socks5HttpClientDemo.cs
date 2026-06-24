using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;

internal sealed class Socks5HttpClientDemo
{
    private readonly ILogger<Socks5HttpClientDemo> _logger;

    public Socks5HttpClientDemo(ILogger<Socks5HttpClientDemo> logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(
        string socks5Host,
        int socks5Port,
        string socks5User,
        string socks5Pass,
        CancellationToken cancellationToken = default
    )
    {
        string proxyUri = $"socks5://{socks5Host}:{socks5Port}";

        var proxy = new WebProxy(proxyUri)
        {
            Credentials = new NetworkCredential(socks5User, socks5Pass),
        };

        using var handler = new SocketsHttpHandler { Proxy = proxy, UseProxy = true };

        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        _logger.LogInformation(
            "[HttpClient:SOCKS5] 开始请求 https://api.ipify.org/，代理: {ProxyUri}",
            proxyUri
        );
        var requestStartedAt = DateTimeOffset.Now;
        var requestStopwatch = Stopwatch.StartNew();
        using var response = await httpClient.GetAsync("https://api.ipify.org/", cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        requestStopwatch.Stop();
        _logger.LogInformation(
            "[HttpClient:SOCKS5] 请求完成，开始时间: {StartedAt:HH:mm:ss.fff}，耗时: {ElapsedMs} ms，出口 IP: {Ip}",
            requestStartedAt,
            requestStopwatch.ElapsedMilliseconds,
            content.Trim()
        );
    }
}
