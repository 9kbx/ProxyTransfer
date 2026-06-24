using System.Diagnostics;
using CloakBrowser;
using CloakBrowser.Human;
using Microsoft.Extensions.Logging;
using ProxyTransfer.Tunnel;

internal sealed class Socks5ProxyPuppeteerDemo
{
    private readonly ILogger<Socks5ProxyPuppeteerDemo> _logger;

    public Socks5ProxyPuppeteerDemo(ILogger<Socks5ProxyPuppeteerDemo> logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(IProxyTunnel tunnel, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[Puppeteer] 准备通过本地 HTTP 中转访问目标网站: {LocalProxyUri}",
            tunnel.LocalProxyUri
        );
        _logger.LogInformation(
            "[说明] Chromium 对 SOCKS5 用户名/密码支持不稳定，当前通过本地 HTTP CONNECT 中转规避限制。"
        );

        // var options = new LaunchOptions
        // {
        //     Headless = false,
        //     Args = new[] { $"--proxy-server={tunnel.LocalProxyUri}" },
        // };

        // await using var browser = await CloakLauncher.LaunchAsync(new LaunchOptions
        // {
        //     Headless = false,
        //     Humanize = true,
        //     HumanPreset = HumanPreset.Careful,
        //     HumanConfig = new Dictionary<string, object> { ["typing_delay"] = 90.0 },
        // });
        await using var ctx = await CloakLauncher.LaunchContextAsync(
            new LaunchContextOptions
            {
                Locale = "en-US",
                Timezone = "America/New_York",
                Viewport = (1280, 800),
                ColorScheme = "dark",

                Proxy = tunnel.LocalProxyUri,

                Headless = false,
                Humanize = true,
                HumanPreset = HumanPreset.Careful,
                HumanConfig = new Dictionary<string, object> { ["typing_delay"] = 90.0 },
            }
        );
        var page = await ctx.NewPageAsync();

        _logger.LogInformation(
            "[Puppeteer] 开始请求 https://api.ipify.org/，代理: {ProxyUri} -> {RemoteProxyUri}",
            tunnel.LocalProxyUri,
            tunnel.RemoteProxyUri
        );
        var requestStartedAt = DateTimeOffset.Now;
        var requestStopwatch = Stopwatch.StartNew();
        await page.GotoAsync("https://api.ipify.org/");

        // var content = await page.EvaluateExpressionAsync<string>("document.body.innerText");
        var content = await page.InnerTextAsync("body");
        requestStopwatch.Stop();

        _logger.LogInformation(
            "[Puppeteer] 请求完成，开始时间: {StartedAt:HH:mm:ss.fff}，耗时: {ElapsedMs} ms，出口 IP: {Ip}",
            requestStartedAt,
            requestStopwatch.ElapsedMilliseconds,
            content.Trim()
        );

        Console.ReadLine();
        _logger.LogInformation("[Puppeteer] 关闭浏览器");
        await ctx.CloseAsync();
    }
}
