using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ProxyTransfer.Tunnel;

namespace ProxyTransfer.Api;

public sealed class UpstreamProbeService : BackgroundService
{
    private readonly ProxyTunnelHostOptions _options;
    private readonly UpstreamPoolRegistry _upstreamPools;

    public UpstreamProbeService(
        IOptions<ProxyTunnelHostOptions> options,
        UpstreamPoolRegistry upstreamPools
    )
    {
        _options = options.Value;
        _upstreamPools = upstreamPools;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(Math.Max(5, _options.ProbeIntervalSeconds))
        );

        do
        {
            await ProbeOnceAsync(stoppingToken).ConfigureAwait(false);
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task ProbeOnceAsync(CancellationToken stoppingToken)
    {
        var targets = _upstreamPools.GetProbeTargets();
        foreach (var target in targets)
        {
            try
            {
                await UpstreamProxyProbe
                    .ProbeAsync(
                        target.Endpoint,
                        _options.ProbeTargetHost,
                        _options.ProbeTargetPort,
                        TimeSpan.FromSeconds(Math.Max(1, _options.ProbeTimeoutSeconds)),
                        stoppingToken
                    )
                    .ConfigureAwait(false);
                _upstreamPools.MarkSuccess(target.PoolId, target.UpstreamId);
            }
            catch (Exception ex)
                when (ex
                        is IOException
                            or InvalidOperationException
                            or SocketException
                            or OperationCanceledException
                )
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                _upstreamPools.MarkFailure(target.PoolId, target.UpstreamId, ex);
            }
        }
    }
}
